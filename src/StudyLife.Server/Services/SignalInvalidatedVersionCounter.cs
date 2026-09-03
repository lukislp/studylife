using System.Collections.Concurrent;

namespace StudyLife.Server.Services;

/// <summary>
/// L1 cache in front of <see cref="RedisVersionCounter"/>: keeps each user's counter value in
/// process memory and drops it when the change signal for that user arrives, so the version
/// lookup that used to cost one Redis round trip on EVERY cached GET (sessions, history,
/// settings) is answered locally between writes. Increments go straight through and refresh the
/// local value with Redis' authoritative result.
///
/// Correctness rests on two things: the writer's pod publishes the signal AFTER the INCR
/// (facades in SessionHistoryCacheVersion/SettingsCacheVersion), and a short safety TTL bounds
/// the damage if a signal is ever lost (Redis pub/sub is fire-and-forget) - the worst case is
/// then one stale cache key for at most <see cref="SafetyTtl"/>, exactly the old poll-interval
/// class of staleness, never permanent. The small window between another pod's INCR and its
/// PUBLISH reaching us can hand out the previous version once; the SSE-triggered refetch the same
/// signal causes on the client runs after the invalidation, so the user still sees fresh data.
/// </summary>
public sealed class SignalInvalidatedVersionCounter : IVersionCounter
{
    public static readonly TimeSpan SafetyTtl = TimeSpan.FromSeconds(30);

    private readonly IVersionCounter _inner;
    private readonly ConcurrentDictionary<string, (int Value, DateTime CachedAt)> _local = new(StringComparer.Ordinal);
    private readonly IDisposable _subscription;

    /// <param name="kind">The ChangeKinds value whose signal invalidates this counter (each
    /// counter reacts only to its own kind - a settings write must not evict cached session
    /// versions).</param>
    public SignalInvalidatedVersionCounter(IVersionCounter inner, IChangeSignal signal, string kind)
    {
        _inner = inner;
        _subscription = signal.SubscribeAll((userId, changedKind) =>
        {
            if (changedKind == kind) _local.TryRemove(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), out _);
        });
    }

    public async Task<int> GetValueAsync(string key)
    {
        if (_local.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.CachedAt < SafetyTtl)
            return cached.Value;
        var value = await _inner.GetValueAsync(key);
        _local[key] = (value, DateTime.UtcNow);
        return value;
    }

    public async Task<int> IncrementAsync(string key)
    {
        var value = await _inner.IncrementAsync(key);
        _local[key] = (value, DateTime.UtcNow);
        return value;
    }
}
