namespace StudyLife.Server.Services;

/// <summary>
/// Singleton counter folded into the GetHistory cache key. SessionsController is transient
/// per-request, so a plain field there wouldn't survive across requests - bumping this on every
/// write makes previously cached entries unreachable (and eventually evicted) without having to
/// enumerate/remove individual cache keys for every days/onlyCompleted combination.
///
/// Thin synchronous facade around <see cref="IVersionCounter"/> (in-memory in single-instance
/// mode, Redis in multi-pod mode - see Program.cs) - see the IVersionCounter comment for the
/// design rationale behind the synchronous GetAwaiter().GetResult() delegation.
/// </summary>
public class SessionHistoryCacheVersion
{
    private readonly IVersionCounter _counter;

    public SessionHistoryCacheVersion(IVersionCounter counter)
    {
        _counter = counter;
    }

    /// <summary>
    /// Read: current counter value. Write (only used via <c>Value++</c>): atomically increments
    /// the counter by 1 - the "new" value the compiler computes for <c>Value++</c> is
    /// deliberately ignored, so that even in Redis mode IncrementAsync (a real atomic INCR) is
    /// used instead of a racy read-modify-write.
    /// </summary>
    public int Value
    {
        get => _counter.GetValueAsync().GetAwaiter().GetResult();
        set => _counter.IncrementAsync().GetAwaiter().GetResult();
    }
}
