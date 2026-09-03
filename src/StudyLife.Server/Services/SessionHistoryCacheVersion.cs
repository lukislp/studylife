using System.Globalization;

namespace StudyLife.Server.Services;

/// <summary>
/// Per-user counter folded into the Sessions/History cache keys (SessionsController). The
/// controller is transient per request, so a plain field there wouldn't survive across
/// requests - bumping this on every session write makes the previously cached entry (and the
/// ETag derived from the same key) unreachable for THAT user without touching the cache
/// directly. Other users' entries stay valid: the counter is keyed by AuthUserId (2026-09 audit
/// P4), and every access is async end to end (P3) instead of blocking on a Redis round trip.
///
/// Thin facade around <see cref="IVersionCounter"/> (in-memory in single-instance mode, Redis in
/// multi-pod mode - see Program.cs).
/// </summary>
public class SessionHistoryCacheVersion
{
    private readonly IVersionCounter _counter;
    private readonly IChangeSignal? _signal;

    /// <param name="signal">Published AFTER every bump so connected clients (EventsController)
    /// refetch immediately and other pods drop their local copy of the counter
    /// (SignalInvalidatedVersionCounter). Optional for tests that only need the counter.</param>
    public SessionHistoryCacheVersion(IVersionCounter counter, IChangeSignal? signal = null)
    {
        _counter = counter;
        _signal = signal;
    }

    public Task<int> GetAsync(int authUserId) => _counter.GetValueAsync(Key(authUserId));

    /// <summary>Atomically increments the user's counter (a real INCR in Redis mode, never a
    /// racy read-modify-write) - call after every write that changes that user's sessions.</summary>
    public async Task<int> BumpAsync(int authUserId)
    {
        var value = await _counter.IncrementAsync(Key(authUserId));
        if (_signal is not null) await _signal.PublishAsync(authUserId, ChangeKinds.Sessions);
        return value;
    }

    private static string Key(int authUserId) => authUserId.ToString(CultureInfo.InvariantCulture);
}
