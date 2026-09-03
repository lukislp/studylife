namespace StudyLife.Server.Services;

/// <summary>
/// Abstraction making the two cache invalidation counters (<see cref="SessionHistoryCacheVersion"/>,
/// <see cref="SettingsCacheVersion"/>) swappable between single-instance
/// (<see cref="InMemoryVersionCounter"/>) and multi-pod (<see cref="RedisVersionCounter"/>, a real
/// distributed atomic counter).
///
/// Keyed per user since the 2026-09 audit (P3/P4): the counters used to be one process-wide (or
/// one Redis-wide) integer, so ANY user's write invalidated EVERY user's cached sessions and
/// settings and their ETags - and the facades read them synchronously via GetAwaiter().GetResult()
/// on the request path, which in Redis mode parked a thread-pool thread per cache lookup. Both
/// facades are async now and pass the AuthUserId as the key; the implementations only ever see an
/// opaque key string.
/// </summary>
public interface IVersionCounter
{
    /// <summary>Read the current counter value for a key without changing it (cache key construction). Unknown keys read as 0.</summary>
    Task<int> GetValueAsync(string key);

    /// <summary>Atomically increment a key's counter by 1 and return the new value (invalidation after a write).</summary>
    Task<int> IncrementAsync(string key);
}
