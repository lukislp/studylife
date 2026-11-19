// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// The client's one clock is the <see cref="TimeProvider"/> in DI: every "now" or "today" the UI
/// shows or decides on (the calendar's now line and today column, countdowns, "today" on the
/// dashboard, the sidebar clock, default dates in forms) comes from it, never from
/// <see cref="DateTime.Now"/> directly. The browser registers <see cref="TimeProvider.System"/>; a host
/// can register another one, as the native app's store screenshot mode does to pin the time to the
/// simulator's status bar. Measuring elapsed time (timers, cache ages, cache-busting query values,
/// telemetry timestamps) is not "the time the user sees" and keeps the real clock.
///
/// Session times are naive local values (see the server's "audit finding Z1"), so the UI compares
/// them with the clock's local wall time.
/// </summary>
public static class ClockExtensions
{
    /// <summary>The clock's local wall time, comparable with session start/end times.</summary>
    public static DateTime LocalNow(this TimeProvider clock) => clock.GetLocalNow().DateTime;

    /// <summary>The clock's local date (midnight).</summary>
    public static DateTime LocalToday(this TimeProvider clock) => clock.GetLocalNow().Date;
}
