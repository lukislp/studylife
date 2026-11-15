// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>How a session block in the calendar relates to the current time.</summary>
public enum SessionBlockState
{
    /// <summary>Starts in the future and is not completed.</summary>
    Upcoming,
    /// <summary>Started, not over yet, not completed.</summary>
    Running,
    /// <summary>Ended without being completed.</summary>
    Past,
    /// <summary>Finished with the focus timer (or marked completed). Wins over every time-based state.</summary>
    Done,
}

/// <summary>
/// Geometry and state of one session block in a day column (1 px per minute, 1440 px per day).
/// Pure so the rules live in one tested place and the Razor components only render the result.
/// </summary>
/// <param name="Top">Offset from the top of the day body in px (minutes since midnight).</param>
/// <param name="Height">Height in px. Equals the session's minutes; the only stretch is
/// <see cref="Compute"/>'s <c>minHeightPx</c> so a very short session stays visible.</param>
/// <param name="Compact">True when the block is too short for name + topic + time on three lines
/// and renders a single "name · time" line instead.</param>
/// <param name="State">Done / past / running / upcoming.</param>
public readonly record struct SessionBlockLayout(int Top, double Height, bool Compact, SessionBlockState State)
{
    /// <summary>
    /// Below this many minutes the three-line block does not fit. Derived from calendar.css:
    /// 6 px vertical padding + name (0.7rem) + topic (0.65rem) + time (0.6rem) is about 42 px.
    /// </summary>
    public const int CompactBelowMinutes = 45;

    /// <summary>
    /// Smallest height that keeps a very short session visible and clickable. A block therefore
    /// never ends later than its session by more than this many pixels (minutes). Callers that
    /// carry controls inside the block (planner proposals) pass a larger value.
    /// </summary>
    public const double DefaultMinHeightPx = 5;

    public static SessionBlockLayout Compute(
        DateTime start, DateTime end, bool isCompleted, DateTime now, double minHeightPx = DefaultMinHeightPx)
    {
        var minutes = (end - start).TotalMinutes;
        var state = isCompleted ? SessionBlockState.Done
            : end <= now ? SessionBlockState.Past
            : start <= now ? SessionBlockState.Running
            : SessionBlockState.Upcoming;
        return new SessionBlockLayout(
            start.Hour * 60 + start.Minute,
            Math.Max(minHeightPx, minutes),
            minutes < CompactBelowMinutes,
            state);
    }

    /// <summary>The CSS class for <see cref="State"/> (see calendar.css).</summary>
    public string StateClass => State switch
    {
        SessionBlockState.Done => "is-done",
        SessionBlockState.Past => "is-past",
        SessionBlockState.Running => "is-running",
        _ => "",
    };
}
