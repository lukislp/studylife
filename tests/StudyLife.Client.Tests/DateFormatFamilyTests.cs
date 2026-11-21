// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components.Calendar;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>
/// Every chart axis and hour axis follows ONE date/time family per UI language. The English store
/// screenshots used to mix German "28.09"/"05.26"/"29.08.2034" (labels pre-formatted by the
/// shared summary builders) and a 24h calendar axis with 12h session times.
/// </summary>
[Collection(LocalDateStateCollection.Name)]
public class DateFormatFamilyTests : IDisposable
{
    private readonly string _language = LocalDate.Language;

    public void Dispose() => LocalDate.Language = _language;

    private static readonly DateTime Monday = new(2026, 9, 28);

    [Theory]
    [InlineData("en", "28/09")]
    [InlineData("de", "28.09")]
    public void The_dashboard_trend_axis_formats_the_raw_week_start(string language, string expected)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<DashboardTrendChart>(p => p
            .Add(c => c.Weeks, [new DashboardTrendChart.WeekTrend(Monday, "28.09", 4, 50, false)]));

        Assert.Equal(expected, cut.Find(".trend-bar-label").TextContent.Trim());
    }

    [Fact]
    public void The_dashboard_trend_axis_keeps_the_server_label_without_a_raw_date()
    {
        LocalDate.Language = "en";
        using var ctx = new BunitContext();

        var cut = ctx.Render<DashboardTrendChart>(p => p
            .Add(c => c.Weeks, [new DashboardTrendChart.WeekTrend(null, "28.09", 4, 50, false)]));

        Assert.Equal("28.09", cut.Find(".trend-bar-label").TextContent.Trim());
    }

    [Theory]
    [InlineData("en", "05/26")]
    [InlineData("de", "05.26")]
    public void The_ects_plan_axis_uses_the_month_year_of_the_ui_language(string language, string expected)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsEctsPlanCard>(p => p
            .Add(c => c.Points, [new StatsEctsPlanCard.PlanPoint(new DateTime(2026, 5, 1), "05.26", 5, 20, 10, 40)]));

        Assert.Equal(expected, cut.Find(".timeline-label").TextContent.Trim());
    }

    [Theory]
    [InlineData("en", "28/09")]
    [InlineData("de", "28.09.")]
    public void The_weekly_stats_axes_use_day_month_of_the_ui_language(string language, string expected)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var productivity = ctx.Render<StatsProductivityScoreCard>(p => p
            .Add(c => c.Weeks, [new StatsProductivityScoreCard.WeekPoint(Monday, "28.09.", 80)]));
        var inactivity = ctx.Render<StatsInactivityTrendCard>(p => p
            .Add(c => c.Weeks, [new StatsInactivityTrendCard.WeekBar(Monday, "28.09.", 3, 60)]));

        Assert.Equal(expected, productivity.Find(".mini-bar-label").TextContent.Trim());
        Assert.Equal(expected, inactivity.Find(".mini-bar-label").TextContent.Trim());
    }

    [Fact]
    public void The_english_calendar_hour_axis_uses_the_12h_clock_of_the_session_times()
    {
        LocalDate.Language = "en";
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<PlannerCalendarPreview>(p => p.Add(c => c.ContainerId, "preview"));

        var labels = cut.FindAll(".hour-label").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal("12 AM", labels[0]);
        Assert.Equal("7 AM", labels[7]);
        Assert.Equal("1 PM", labels[13]);
        Assert.DoesNotContain(labels, l => l.Contains(':'));
    }

    [Fact]
    public void The_german_calendar_hour_axis_stays_24h()
    {
        LocalDate.Language = "de";
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<PlannerCalendarPreview>(p => p.Add(c => c.ContainerId, "preview"));

        var labels = cut.FindAll(".hour-label").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal("07:00", labels[7]);
        Assert.Equal("24:00", labels[^1]);
    }
}
