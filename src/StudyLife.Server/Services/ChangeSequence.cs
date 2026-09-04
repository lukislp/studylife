using System.Globalization;

namespace StudyLife.Server.Services;

/// <summary>
/// Per-user "anything changed" counter, incremented by <see cref="ChangeBroadcastFilter"/> on
/// every successful write and sent in every GET api/events?v=2 frame (connect, heartbeat,
/// change). A client compares it with the last value it saw: equal means it missed nothing
/// while disconnected or while a frame got lost; different without a moved sessions/settings
/// counter means some other kind changed and the pages refetch what they show. Together with
/// the two cache-version counters this replaces the 30 s timer poll entirely.
/// </summary>
public sealed class ChangeSequence
{
    private readonly IVersionCounter _counter;

    public ChangeSequence(IVersionCounter counter) => _counter = counter;

    public Task<int> GetAsync(int authUserId) => _counter.GetValueAsync(Key(authUserId));

    public Task<int> IncrementAsync(int authUserId) => _counter.IncrementAsync(Key(authUserId));

    private static string Key(int authUserId) => authUserId.ToString(CultureInfo.InvariantCulture);
}
