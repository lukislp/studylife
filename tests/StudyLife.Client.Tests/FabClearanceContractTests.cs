// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;

namespace StudyLife.Client.Tests;

/// <summary>
/// App Store screenshots (v3.39.12): the floating action button covered the iPad dashboard
/// donut's percentage column and the last axis label of a stats chart on the iPhone, and the
/// stats header's second action was indented when it wrapped. No test renders real layout, so
/// this pins the CSS contract that keeps content clear of the button: a reserved right lane on
/// wide layouts, bottom clearance on phones, both derived from the button's own geometry.
/// </summary>
public class FabClearanceContractTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    private static string Css(string name) => RepoFile("src", "StudyLife.Client", "wwwroot", "css", name);

    /// <summary>Body of the first rule whose selector is exactly <paramref name="selector"/>,
    /// searched after <paramref name="from"/> (to reach the rule inside a media query).</summary>
    private static string Rule(string css, string selector, string? from = null)
    {
        var start = from is null ? 0 : css.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker '{from}' not found");
        var match = new Regex(Regex.Escape(selector) + @"\s*\{([^}]*)\}").Match(css, start);
        Assert.True(match.Success, $"rule '{selector}' not found");
        return match.Groups[1].Value;
    }

    [Fact]
    public void The_button_geometry_is_defined_once_and_used_by_the_button()
    {
        var css = Css("base.css");

        Assert.Contains("--fab-size: 52px;", css);
        Assert.Contains("--fab-lane: calc(var(--fab-size) + var(--fab-inset) + 0.5rem);", css);
        var main = Rule(css, ".fab-main");
        Assert.Contains("width: var(--fab-size);", main);
        Assert.Contains("height: var(--fab-size);", main);
        Assert.Contains("right: var(--fab-inset); bottom: var(--fab-inset);", css);
    }

    [Fact]
    public void Wide_layouts_reserve_the_buttons_lane_on_the_right()
    {
        var css = Css("base.css");

        Assert.Contains("padding-right: max(0px, calc(var(--fab-lane) - 2rem));", Rule(css, ".main-content"));
        Assert.Contains("padding-right: var(--fab-lane);", Rule(css, ".app-shell.page-calendar .main-content"));
    }

    [Fact]
    public void Phones_keep_the_end_of_every_page_above_the_button()
    {
        var css = Css("responsive.css");

        var main = Rule(css, ".main-content", from: "@media (max-width: 768px)");
        Assert.Contains("padding-right: 0;", main);
        Assert.Contains("padding-bottom: calc(var(--fab-size) + 1.5rem);", main);
    }

    [Fact]
    public void The_stats_header_actions_wrap_flush_left()
    {
        var markup = RepoFile("src", "StudyLife.Client", "Pages", "Stats.razor");
        var css = Css("stats.css");

        Assert.Matches(new Regex(@"<div class=""stats-header-actions"">\s*<a class=""wrapped-trigger-btn""[^\n]*\n\s*<ReportExportLink />\s*</div>"), markup);
        var actions = Rule(css, ".stats-header-actions");
        Assert.Contains("flex-wrap: wrap;", actions);
        Assert.Contains("gap: 0.75rem;", actions);
        Assert.DoesNotContain("margin-left", Rule(css, ".report-export-link"));
    }
}
