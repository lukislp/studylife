// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Calendar;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;

namespace StudyLife.Client.Tests;

/// <summary>A clock that always reads the same local wall time (UTC as its zone, so local = UTC).</summary>
internal sealed class FixedLocalClock(DateTime local) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>
/// The UI's "now" comes from the TimeProvider in DI (ClockExtensions), not from DateTime.Now: with a
/// clock pinned to 09:41 (the native app's store screenshot mode), the calendar's now line, today's
/// column, the countdown to the next session, today's session states and the sidebar clock all show
/// 09:41 - whatever the real time is.
/// </summary>
[Collection(LocalDateStateCollection.Name)]
public class PinnedClockTests : IDisposable
{
    private static readonly DateTime Pinned = new(2026, 11, 19, 9, 41, 0);
    private readonly BunitContext _ctx = new();

    public PinnedClockTests()
    {
        _ctx.Services.AddSingleton<TimeProvider>(new FixedLocalClock(Pinned));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose()
    {
        LocalDate.ResetNames();
        _ctx.Dispose();
    }

    [Fact]
    public void The_extensions_read_local_wall_time_and_date_from_the_clock()
    {
        var clock = new FixedLocalClock(Pinned);

        Assert.Equal(Pinned, clock.LocalNow());
        Assert.Equal(Pinned.Date, clock.LocalToday());
    }

    [Fact]
    public void The_calendar_now_line_sits_at_the_pinned_minute_of_the_pinned_day()
    {
        var cut = _ctx.Render<CalendarDayColumn>(p => p.Add(c => c.Date, Pinned.Date));

        var line = cut.Find(".cal-now-line");
        Assert.Contains($"top:{9 * 60 + 41}px", line.GetAttribute("style"));
        Assert.Contains("today", cut.Find(".cal-day").ClassName);
    }

    [Fact]
    public void Another_day_has_no_now_line_and_the_day_before_counts_as_past()
    {
        var cut = _ctx.Render<CalendarDayColumn>(p => p.Add(c => c.Date, Pinned.Date.AddDays(-1)));

        Assert.Empty(cut.FindAll(".cal-now-line"));
        Assert.Contains("past", cut.Find(".cal-day").ClassName);
    }

    [Fact]
    public void The_next_session_countdown_counts_from_the_pinned_time()
    {
        var next = new StudySession { CourseName = "Math", StartTime = Pinned.Date.AddHours(14), EndTime = Pinned.Date.AddHours(15.5) };

        var cut = _ctx.Render<DashboardNextSessionCard>(p => p
            .Add(c => c.UpcomingSession, next)
            .Add(c => c.StartsAtFormat, "{0} ({1})"));

        Assert.EndsWith("(4h 19m)", cut.Find(".nc-time").TextContent);
    }

    [Fact]
    public void Todays_sessions_are_done_or_upcoming_relative_to_the_pinned_time()
    {
        var done = new StudySession { CourseName = "Math", StartTime = Pinned.Date.AddHours(7.5), EndTime = Pinned.Date.AddHours(8.5) };
        var later = new StudySession { CourseName = "Stats", StartTime = Pinned.Date.AddHours(14), EndTime = Pinned.Date.AddHours(15.5) };

        var cut = _ctx.Render<DashboardTodaySessionsCard>(p => p.Add(c => c.Sessions, [done, later]));

        var rows = cut.FindAll(".session-row");
        Assert.Contains("done", rows[0].ClassName);
        Assert.Contains("upcoming", rows[1].ClassName);
    }

    [Fact]
    public void The_sidebar_clock_shows_the_pinned_time()
    {
        LocalDate.ApplyNames("de", null);

        var cut = _ctx.Render<SidebarClock>();

        Assert.Equal(LocalDate.Time(Pinned), cut.Find(".time-display").TextContent);
        Assert.Equal($"{LocalDate.Weekday(Pinned, true)}, {LocalDate.DayMonth(Pinned)}", cut.Find(".date-display").TextContent);
    }
}
