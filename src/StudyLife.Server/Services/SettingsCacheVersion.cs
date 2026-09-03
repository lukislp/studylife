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
    private readonly IChangeSignal? _signal;

    /// <param name="signal">Published AFTER every bump so connected clients (EventsController)
    /// refetch immediately and other pods drop their local copy of the counter
    /// (SignalInvalidatedVersionCounter). Optional for tests that only need the counter.</param>
    public SettingsCacheVersion(IVersionCounter counter, IChangeSignal? signal = null)
    {
        _counter = counter;
        _signal = signal;
    }

    public Task<int> GetAsync(int authUserId) => _counter.GetValueAsync(Key(authUserId));

    /// <summary>Atomically increments the user's counter (a real INCR in Redis mode, never a
    /// racy read-modify-write) - call after every write that changes that user's settings.</summary>
    public async Task<int> BumpAsync(int authUserId)
    {
        var value = await _counter.IncrementAsync(Key(authUserId));
        if (_signal is not null) await _signal.PublishAsync(authUserId, ChangeKinds.Settings);
        return value;
    }

    private static string Key(int authUserId) => authUserId.ToString(CultureInfo.InvariantCulture);
}
