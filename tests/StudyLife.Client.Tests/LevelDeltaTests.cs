// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Components.Stats;

namespace StudyLife.Client.Tests;

/// <summary>No change, no arrow: a missing delta label hides the whole delta.</summary>
public class LevelDeltaTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_month_card_shows_no_comparison_rows_without_labels()
    {
        var cut = _ctx.Render<DashboardMonthComparisonCard>(p => p
            .Add(c => c.CurrentLabel, "4h")
            .Add(c => c.HasYearData, true));

        Assert.Empty(cut.FindAll(".stat-delta"));
    }

    [Fact]
    public void The_month_card_shows_the_rows_that_have_a_label()
    {
        var cut = _ctx.Render<DashboardMonthComparisonCard>(p => p
            .Add(c => c.CurrentLabel, "4h")
            .Add(c => c.VsLastMonthLabel, "1h")
            .Add(c => c.VsLastMonthUp, true)
            .Add(c => c.HasYearData, true));

        Assert.Contains("1h", cut.Find(".stat-delta").TextContent);
        Assert.Single(cut.FindAll(".stat-delta"));
    }

    [Fact]
    public void The_stats_summary_shows_no_arrow_without_a_label()
    {
        var cut = _ctx.Render<StatsSummaryCard>(p => p.Add(c => c.TotalHoursLabel, "10h"));

        Assert.Empty(cut.FindAll(".stat-delta"));
    }
}
