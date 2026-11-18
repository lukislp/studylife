// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Models;

namespace StudyLife.Client.Services;

/// <summary>
/// Where the calendar grid should be scrolled to: <see cref="Minute"/> is the minute of the day
/// (the grid is 1 px per minute) that is placed at <see cref="ViewportFraction"/> of the visible
/// height, so a value above 0 keeps some context above the target in view.
/// </summary>
public readonly record struct CalendarScrollTarget(int Minute, double ViewportFraction);

/// <summary>
/// The pure decision behind the calendar's initial scroll position, shared by the week and the
/// day view: the current time when today is visible, otherwise the first session of the visible
/// days, otherwise a sensible morning default. It knows nothing about the DOM; the page passes the
/// result to the JS helper that does the actual scrolling.
/// </summary>
public static class CalendarInitialScroll
{
    /// <summary>Opening position when there is neither a "now" line nor a session: 07:00 at the top.</summary>
    public const int DefaultMinute = 7 * 60;

    /// <summary>Share of the viewport kept above "now" / the first session.</summary>
    public const double ContextFraction = 0.3;

    public static CalendarScrollTarget Resolve(
        DateTime now,
        IReadOnlyCollection<DateTime> visibleDays,
        IEnumerable<StudySession> visibleSessions)
    {
        if (visibleDays.Any(d => d.Date == now.Date))
        {
            return new CalendarScrollTarget(now.Hour * 60 + now.Minute, ContextFraction);
        }

        var days = visibleDays.Select(d => d.Date).ToHashSet();
        var first = visibleSessions
            .Where(s => days.Contains(s.StartTime.Date))
            .Select(s => (int?)(s.StartTime.Hour * 60 + s.StartTime.Minute))
            .Min();

        return first is { } minute
            ? new CalendarScrollTarget(minute, ContextFraction)
            : new CalendarScrollTarget(DefaultMinute, 0);
    }
}
