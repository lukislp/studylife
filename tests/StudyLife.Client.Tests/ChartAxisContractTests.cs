// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;
using Bunit;
using StudyLife.Client.Components.Stats;

namespace StudyLife.Client.Tests;

/// <summary>
/// v3.39.13 store screenshots: the last axis label of a stats chart ended flush against the
/// card / scroll edge (iPad "ECTS: actual vs. plan", German iPhone "ECTS progress over time").
/// Every column chart now shares three classes from shared.css - .chart-plot (inner edge
/// padding), .chart-scroll (the sideways scroller around a wide chart) and .chart-axis-label
/// (never wrapped, ellipsized or clipped). This pins both the CSS and that every chart uses them.
/// </summary>
[Collection(LocalDateStateCollection.Name)]
public class ChartAxisContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Css(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "StudyLife.Client", "wwwroot", "css", name));

    private static string Rule(string css, string selector)
    {
        var match = new Regex(@"(?m)^" + Regex.Escape(selector) + @"\s*\{([^}]*)\}").Match(css);
        Assert.True(match.Success, $"rule '{selector}' not found");
        return match.Groups[1].Value;
    }

    /// <summary>Every chart component: the stats cards plus the dashboard's "last 8 weeks" trend.</summary>
    private static IEnumerable<string> ChartMarkupFiles()
    {
        var components = Path.Combine(RepoRoot(), "src", "StudyLife.Client", "Components");
        return Directory.GetFiles(Path.Combine(components, "Stats"), "*.razor")
            .Append(Path.Combine(components, "Dashboard", "DashboardTrendChart.razor"));
    }

    private static IEnumerable<string> ClassAttributes(string markup) =>
        Regex.Matches(markup, @"class=""([^""]*)""").Select(m => m.Groups[1].Value);

    [Fact]
    public void The_shared_chart_classes_pad_the_plot_and_never_clip_a_label()
    {
        var css = Css("shared.css");

        Assert.Contains("--chart-edge-pad:", css);
        Assert.Contains("padding-inline: var(--chart-edge-pad);", Rule(css, ".chart-plot"));
        var scroll = Rule(css, ".chart-scroll");
        Assert.Contains("overflow-x: auto;", scroll);
        Assert.Contains("scroll-padding-inline: var(--chart-edge-pad);", scroll);
        var plotInScroll = Rule(css, ".chart-scroll > .chart-plot");
        Assert.Contains("width: max-content;", plotInScroll);
        Assert.Contains("min-width: 100%;", plotInScroll);
        var label = Rule(css, ".chart-axis-label");
        Assert.Contains("white-space: nowrap;", label);
        Assert.Contains("overflow: visible;", label);
        Assert.DoesNotContain("ellipsis", label);
    }

    [Fact]
    public void No_chart_row_or_axis_label_ellipsizes_or_scrolls_on_its_own()
    {
        var css = Css("stats.css") + Css("dashboard.css");

        foreach (var selector in new[] { ".mini-bar-label", ".timeline-label", ".grouped-label", ".stacked-label", ".trend-bar-label" })
        {
            Assert.DoesNotMatch(new Regex(Regex.Escape(selector) + @"[^{]*\{[^}]*(ellipsis|overflow\s*:\s*hidden)"), css);
        }
        // The rows scroll inside a .chart-scroll wrapper only: a flex row that is its own scroll
        // container loses its end padding in WebKit and clips the last column again.
        foreach (var selector in new[] { ".timeline-chart", ".grouped-chart", ".productivity-chart", ".inactivity-chart", ".mini-bar-chart" })
        {
            Assert.DoesNotContain("overflow", Rule(css, selector));
        }
    }

    [Fact]
    public void Every_chart_uses_the_shared_plot_and_axis_label_classes()
    {
        var rows = new[] { "mini-bar-chart", "timeline-chart", "grouped-chart", "stacked-chart", "trend-chart" };
        var labels = new[] { "mini-bar-label", "timeline-label", "grouped-label", "stacked-label", "trend-bar-label" };
        var checkedRows = 0;

        foreach (var file in ChartMarkupFiles())
        {
            foreach (var classes in ClassAttributes(File.ReadAllText(file)))
            {
                var tokens = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Intersect(rows).Any())
                {
                    checkedRows++;
                    Assert.True(tokens.Contains("chart-plot"), $"{Path.GetFileName(file)}: '{classes}' lacks chart-plot");
                }
                if (tokens.Intersect(labels).Any())
                {
                    Assert.True(tokens.Contains("chart-axis-label"), $"{Path.GetFileName(file)}: '{classes}' lacks chart-axis-label");
                }
            }
        }
        Assert.True(checkedRows >= 15, $"only {checkedRows} chart rows found - did the chart markup move?");
    }

    [Fact]
    public void The_ects_plan_chart_scrolls_in_the_shared_wrapper_with_padded_edges()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsEctsPlanCard>(p => p
            .Add(c => c.Points,
            [
                new StatsEctsPlanCard.PlanPoint(new DateTime(2026, 5, 1), "05.26", 5, 20, 10, 40),
                new StatsEctsPlanCard.PlanPoint(new DateTime(2026, 6, 1), "06.26", null, null, 20, 80),
            ]));

        var plot = cut.Find(".chart-scroll > .timeline-chart.chart-plot");
        Assert.Equal(2, plot.QuerySelectorAll(".timeline-label.chart-axis-label").Length);
    }

    [Fact]
    public void The_weekly_trend_charts_scroll_in_the_shared_wrapper()
    {
        var monday = new DateTime(2026, 9, 28);
        using var ctx = new BunitContext();

        var productivity = ctx.Render<StatsProductivityScoreCard>(p => p
            .Add(c => c.Weeks, [new StatsProductivityScoreCard.WeekPoint(monday, "28.09.", 80)]));
        var inactivity = ctx.Render<StatsInactivityTrendCard>(p => p
            .Add(c => c.Weeks, [new StatsInactivityTrendCard.WeekBar(monday, "28.09.", 3, 60)]));

        Assert.Single(productivity.FindAll(".chart-scroll > .productivity-chart.chart-plot .mini-bar-label.chart-axis-label"));
        Assert.Single(inactivity.FindAll(".chart-scroll > .inactivity-chart.chart-plot .mini-bar-label.chart-axis-label"));
    }
}
