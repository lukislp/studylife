// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SampleData;

namespace StudyLife.Server.Tests;

/// <summary>The demo's month comparison must not show an artificial drop: the app compares the month
/// in progress with the whole earlier month, so the sample history thins the earlier month out
/// (SampleDataset.ShapeMonthTrend).</summary>
public class DemoMonthTrendTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DemoMonthTrendTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private static StudySessionEntity Session(DateTime day, double hours) => new()
    {
        StartTime = day.AddHours(10),
        EndTime = day.AddHours(10 + hours),
    };

    private static double Hours(IEnumerable<StudySessionEntity> sessions) =>
        sessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);

    [Fact]
    public void An_earlier_month_heavier_than_the_current_one_is_thinned_out_but_keeps_the_streak_days()
    {
        var today = new DateTime(2026, 11, 20);
        var history = new List<StudySessionEntity>();
        for (var d = new DateTime(2026, 10, 1); d < today; d = d.AddDays(1)) history.Add(Session(d, 2));

        SampleDataset.ShapeMonthTrend(history, today);

        var thisMonth = Hours(history.Where(s => s.StartTime.Month == 11));
        var earlier = Hours(history.Where(s => s.StartTime.Month == 10));
        Assert.True(earlier <= thisMonth * 0.8 + 2, $"earlier {earlier}h vs this month {thisMonth}h");
        Assert.True(earlier > 0);
    }

    [Fact]
    public void The_live_streak_days_of_the_earlier_month_are_never_removed()
    {
        var today = new DateTime(2026, 11, 5);
        var history = new List<StudySessionEntity>();
        for (var d = new DateTime(2026, 10, 1); d < today; d = d.AddDays(1)) history.Add(Session(d, 2));

        SampleDataset.ShapeMonthTrend(history, today);

        // the 12 days before today: 24.10. - 04.11.; the earlier month holds 24.10. - 31.10. of them
        Assert.Equal(8, history.Count(s => s.StartTime >= new DateTime(2026, 10, 24) && s.StartTime < new DateTime(2026, 11, 1)));
        Assert.DoesNotContain(history, s => s.StartTime < new DateTime(2026, 10, 24));
    }

    [Fact]
    public void An_earlier_month_that_is_already_lighter_is_left_alone()
    {
        var today = new DateTime(2026, 11, 20);
        var history = new List<StudySessionEntity>
        {
            Session(new DateTime(2026, 10, 3), 1),
            Session(new DateTime(2026, 11, 2), 5),
        };

        SampleDataset.ShapeMonthTrend(history, today);

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Without_history_in_the_current_month_nothing_changes()
    {
        var today = new DateTime(2026, 11, 1);
        var history = new List<StudySessionEntity> { Session(new DateTime(2026, 10, 3), 3) };

        SampleDataset.ShapeMonthTrend(history, today);

        Assert.Single(history);
    }

    [Theory]
    [InlineData(2026, 11, 20)]
    [InlineData(2026, 11, 12)]
    [InlineData(2026, 12, 28)]
    public async Task The_seeded_demo_never_studies_less_this_month_than_last(int year, int month, int day)
    {
        var now = new DateTime(year, month, day, 9, 41, 0);
        await _factory.WithDbAsync(db => DemoSeeder.ReseedAsync(db, now));
        var sessions = await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.IsCompleted).ToListAsync());

        var earlier = now.AddMonths(-1);
        var thisMonth = Hours(sessions.Where(s => s.StartTime.Year == now.Year && s.StartTime.Month == now.Month));
        var lastMonth = Hours(sessions.Where(s => s.StartTime.Year == earlier.Year && s.StartTime.Month == earlier.Month));

        Assert.True(thisMonth >= lastMonth, $"this month {thisMonth}h < last month {lastMonth}h");
    }
}
