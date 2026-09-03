using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>
/// Multi-pod implementation: a REAL distributed atomic counter via Redis' INCR
/// (<c>StringIncrementAsync</c>), so that all instances behind the load balancer see the same
/// invalidation state - unlike InMemoryVersionCounter, which would be independent per pod and
/// could thus never invalidate other pods' cache entries. One Redis key per (prefix, key), e.g.
/// <c>version:sessionhistory:42</c> for user 42 - a write by one user therefore no longer
/// invalidates every other user's cache and ETag (2026-09 audit P4).
/// </summary>
public class RedisVersionCounter : IVersionCounter
{
    private readonly IDatabase _db;
    private readonly string _keyPrefix;

    public RedisVersionCounter(IConnectionMultiplexer connectionMultiplexer, string keyPrefix)
    {
        _db = connectionMultiplexer.GetDatabase();
        _keyPrefix = keyPrefix;
    }

    public async Task<int> GetValueAsync(string key)
    {
        var value = await _db.StringGetAsync($"{_keyPrefix}:{key}");
        return value.HasValue ? (int)value : 0;
    }

    public async Task<int> IncrementAsync(string key) => (int)await _db.StringIncrementAsync($"{_keyPrefix}:{key}");
}
