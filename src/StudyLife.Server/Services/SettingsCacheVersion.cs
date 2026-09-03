using System.Globalization;

namespace StudyLife.Server.Services;

/// <summary>
/// Per-user counter folded into the Settings cache key (SettingsController.Get). Same design as
/// <see cref="SessionHistoryCacheVersion"/>: bumping after a write makes that user's cached
/// settings (and the ETag derived from the key) unreachable without touching the cache, other
/// users are unaffected, and every access is async (2026-09 audit P3/P4).
/// </summary>
public class SettingsCacheVersion
{
    private readonly IVersionCounter _counter;

    public SettingsCacheVersion(IVersionCounter counter)
    {
        _counter = counter;
    }

    public Task<int> GetAsync(int authUserId) => _counter.GetValueAsync(Key(authUserId));

    /// <summary>Atomically increments the user's counter (a real INCR in Redis mode, never a
    /// racy read-modify-write) - call after every write that changes that user's settings.</summary>
    public Task<int> BumpAsync(int authUserId) => _counter.IncrementAsync(Key(authUserId));

    private static string Key(int authUserId) => authUserId.ToString(CultureInfo.InvariantCulture);
}
