namespace StudyLife.Server.Services;

/// <summary>
/// Default implementation (single-instance/SQLite mode): exactly the previous behavior of
/// SessionHistoryCacheVersion/SettingsCacheVersion before the scalability rework - a simple
/// in-process int, atomic via Interlocked/Volatile. Only correct within a SINGLE process; in
/// multi-pod operation, RedisVersionCounter must be registered instead (see Program.cs).
/// </summary>
public class InMemoryVersionCounter : IVersionCounter
{
    private int _value;

    public Task<int> GetValueAsync() => Task.FromResult(Volatile.Read(ref _value));

    public Task<int> IncrementAsync() => Task.FromResult(Interlocked.Increment(ref _value));
}
