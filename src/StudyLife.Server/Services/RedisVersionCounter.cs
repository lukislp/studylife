using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>
/// Multi-pod implementation: a REAL distributed atomic counter via Redis' INCR
/// (<c>StringIncrementAsync</c>), so that all instances behind the load balancer see the same
/// invalidation state - unlike InMemoryVersionCounter, which would be independent per pod and
/// could thus never invalidate other pods' cache entries.
/// </summary>
public class RedisVersionCounter : IVersionCounter
{
    private readonly IDatabase _db;
    private readonly string _key;

    public RedisVersionCounter(IConnectionMultiplexer connectionMultiplexer, string key)
    {
        _db = connectionMultiplexer.GetDatabase();
        _key = key;
    }

    public async Task<int> GetValueAsync()
    {
        var value = await _db.StringGetAsync(_key);
        return value.HasValue ? (int)value : 0;
    }

    public async Task<int> IncrementAsync() => (int)await _db.StringIncrementAsync(_key);
}
