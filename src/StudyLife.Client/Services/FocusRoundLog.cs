// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>One stretch of focus time that really ran: from the moment the timer started or
/// resumed to the moment it paused or the round ended (UTC).</summary>
public sealed record FocusInterval(DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Remembers when focus time really ran, so a consumer that records finished rounds (the native
/// app writes Mindful Minutes to Apple Health) can store the real intervals instead of assuming a
/// round ended now and lasted exactly FocusMinutes. A pause closes the current interval and the
/// resume opens a new one, so paused time is never counted; a round that is stopped or reset
/// before its focus phase ends leaves nothing behind. Pure bookkeeping on timestamps passed in by
/// <see cref="TimerService"/>, which calls it under its own lock.
/// </summary>
public sealed class FocusRoundLog
{
    private readonly List<FocusInterval> _currentRound = [];
    private readonly List<FocusInterval> _finished = [];
    private DateTime? _runningSince;

    /// <summary>Focus time starts or resumes at <paramref name="atUtc"/>.</summary>
    public void Started(DateTime atUtc) => _runningSince ??= atUtc;

    /// <summary>The timer was paused at <paramref name="atUtc"/>: closes the running interval.</summary>
    public void Paused(DateTime atUtc) => CloseRunningInterval(atUtc);

    /// <summary>The focus phase ended at <paramref name="atUtc"/>: the round's intervals become
    /// available through <see cref="TakeFinished"/>.</summary>
    public void RoundEnded(DateTime atUtc)
    {
        CloseRunningInterval(atUtc);
        _finished.AddRange(_currentRound);
        _currentRound.Clear();
    }

    /// <summary>The round was abandoned (stop, reset, another mode): forget its intervals.</summary>
    public void Abandoned()
    {
        _currentRound.Clear();
        _runningSince = null;
    }

    /// <summary>Hands over the intervals of every focus round that ended since the last call.</summary>
    public IReadOnlyList<FocusInterval> TakeFinished()
    {
        var result = _finished.ToArray();
        _finished.Clear();
        return result;
    }

    private void CloseRunningInterval(DateTime atUtc)
    {
        if (_runningSince is { } since && atUtc > since)
            _currentRound.Add(new FocusInterval(since, atUtc));
        _runningSince = null;
    }
}
