using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>
/// Fixed-window limiter whose counter lives in Redis, so all web replicas share ONE bucket per
/// partition. The framework's in-memory FixedWindowRateLimiter is per process: with the HPA at
/// four web pods every client IP effectively had four times the documented limit (4 x 300/min
/// on the API, 4 x 5 recovery-code attempts per 15 minutes), and which pod a given request
/// landed on was the load balancer's coin flip (2026-09 audit, rate-limiter section).
///
/// INCR + PEXPIRE on first hit: the same shape Redis' own rate-limiting pattern documents.
/// PEXPIRE is set only when INCR returned 1 (a brand-new window), so a window can never be
/// extended by later hits. The two commands are not atomic with each other - if the process
/// dies between them the key would live forever - so PEXPIRE is re-issued whenever the key
/// reports no TTL, which heals that corner case on the next request instead of leaking a
/// permanent bucket. Redis being unreachable fails OPEN (the request is allowed and the error
/// is left to the connection's own logging): the limiter is abuse protection, and a Redis
/// outage must not take the whole API down with it.
/// </summary>
public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private readonly IDatabase _db;
    private readonly RedisKey _key;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private DateTime _lastUse = DateTime.UtcNow;

    public RedisFixedWindowRateLimiter(IConnectionMultiplexer connection, string partitionKey, int permitLimit, TimeSpan window)
    {
        _db = connection.GetDatabase();
        _key = $"ratelimit:{partitionKey}";
        _permitLimit = permitLimit;
        _window = window;
    }

    public override TimeSpan? IdleDuration => DateTime.UtcNow - _lastUse;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        _lastUse = DateTime.UtcNow;
        if (permitCount == 0) return Lease.Acquired;
        try
        {
            var count = await _db.StringIncrementAsync(_key, permitCount);
            if (count == permitCount)
            {
                await _db.KeyExpireAsync(_key, _window, CommandFlags.None);
            }
            if (count <= _permitLimit) return Lease.Acquired;

            var ttl = await _db.KeyTimeToLiveAsync(_key);
            if (ttl is null)
            {
                // Healed here: INCR succeeded earlier but PEXPIRE never landed (see class doc).
                await _db.KeyExpireAsync(_key, _window, CommandFlags.None);
                ttl = _window;
            }
            return new Lease(false, ttl.Value);
        }
        catch (RedisException)
        {
            return Lease.Acquired; // fail open, see class doc
        }
    }

    private sealed class Lease : RateLimitLease
    {
        public static readonly Lease Acquired = new(true, null);

        private readonly TimeSpan? _retryAfter;

        public Lease(bool isAcquired, TimeSpan? retryAfter)
        {
            IsAcquired = isAcquired;
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames =>
            _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = _retryAfter.Value;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
