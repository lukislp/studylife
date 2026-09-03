using System.Collections.Concurrent;

namespace StudyLife.Server.Services;

/// <summary>
/// Single-instance implementation (default / SQLite mode, also the public demo host): one
/// process-local counter per key. Never blocks - the Task-returning shape only exists so the
/// same facades can sit in front of <see cref="RedisVersionCounter"/> in multi-pod mode.
/// </summary>
public class InMemoryVersionCounter : IVersionCounter
{
    private readonly ConcurrentDictionary<string, int> _values = new(StringComparer.Ordinal);

    public Task<int> GetValueAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : 0);

    public Task<int> IncrementAsync(string key) =>
        Task.FromResult(_values.AddOrUpdate(key, 1, (_, current) => current + 1));
}
