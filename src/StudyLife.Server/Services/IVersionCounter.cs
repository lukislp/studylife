namespace StudyLife.Server.Services;

/// <summary>
/// Abstraction making the two process-wide cache invalidation counters
/// (<see cref="SessionHistoryCacheVersion"/>, <see cref="SettingsCacheVersion"/>) swappable
/// between single-instance (<see cref="InMemoryVersionCounter"/>, exactly the behavior from
/// before the scalability rework) and multi-pod (<see cref="RedisVersionCounter"/>, a real
/// distributed atomic counter).
///
/// Design decision Value++/async: the roughly dozen existing call sites
/// (e.g. <c>_historyCacheVersion.Value++</c>, <c>$"...{_settingsCacheVersion.Value}"</c>)
/// are synchronous property accesses. Instead of converting all of them to
/// <c>await IncrementAsync()</c>/<c>await GetValueAsync()</c>,
/// <see cref="SessionHistoryCacheVersion"/>/<see cref="SettingsCacheVersion"/> themselves remain
/// thin synchronous facades (<c>GetAwaiter().GetResult()</c>) around this interface. For the
/// in-memory case (default/SQLite mode) this is synchronous-ready anyway (Task.FromResult) and
/// never actually blocks; in Redis mode it briefly blocks on a network round trip - a
/// deliberately accepted trade-off for this learning project against the much larger effort of
/// converting all existing call sites to async.
/// </summary>
public interface IVersionCounter
{
    /// <summary>Read the current counter value without changing it (cache key construction).</summary>
    Task<int> GetValueAsync();

    /// <summary>Atomically increment the counter by 1 and return the new value (invalidation after a write).</summary>
    Task<int> IncrementAsync();
}
