// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>Numbers follow the UI language, not the (always invariant) process culture: the English
/// UI used to show a German "1,66" because grade labels were hard-wired to the comma.</summary>
[Collection(LocalDateStateCollection.Name)]
public class LocalNumberTests : IDisposable
{
    private readonly string _language = LocalDate.Language;

    public void Dispose() => LocalDate.Language = _language;

    [Theory]
    [InlineData("en", "1.66")]
    [InlineData("en-GB", "1.66")]
    [InlineData("ga", "1.66")]
    [InlineData("de", "1,66")]
    [InlineData("fr", "1,66")]
    public void Grades_use_the_decimal_separator_of_the_ui_language(string language, string expected)
    {
        LocalDate.Language = language;

        Assert.Equal(expected, LocalNumber.Grade(1.66m));
    }

    [Theory]
    [InlineData("en", "1,66", "1.66")]
    [InlineData("en", "–", "–")]
    [InlineData("en", "12", "12")]
    [InlineData("de", "1,66", "1,66")]
    public void Server_labels_are_re_expressed_in_the_ui_language(string language, string label, string expected)
    {
        LocalDate.Language = language;

        Assert.Equal(expected, LocalNumber.Localize(label));
    }

    [Fact]
    public void A_missing_label_stays_missing()
    {
        LocalDate.Language = "en";

        Assert.Null(LocalNumber.Localize(null));
    }

    [Fact]
    public void Plain_numbers_follow_the_ui_language()
    {
        LocalDate.Language = "de";
        Assert.Equal("2,5", LocalNumber.Number(2.5, "0.#"));

        LocalDate.Language = "en";
        Assert.Equal("2.5", LocalNumber.Number(2.5m, "0.#"));
    }

    [Fact]
    public void The_stats_summary_card_shows_the_average_grade_in_the_english_ui_with_a_point()
    {
        LocalDate.Language = "en";
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsSummaryCard>(p => p
            .Add(c => c.AverageGradeLabel, "1,66")
            .Add(c => c.AvgGradeLabel, "Avg. grade (weighted)"));

        Assert.Contains("1.66", cut.Markup);
        Assert.DoesNotContain("1,66", cut.Markup);
    }

    [Theory]
    [InlineData("en", "1,3", 1.3)]
    [InlineData("en", "1.3", 1.3)]
    [InlineData("de", "1,3", 1.3)]
    [InlineData("de", "1.3", 1.3)]
    [InlineData("en", " 2,75 ", 2.75)]
    [InlineData("de", "3", 3)]
    public void A_typed_grade_accepts_comma_and_point_in_every_language(string language, string text, double expected)
    {
        LocalDate.Language = language;

        Assert.True(LocalNumber.TryParseGrade(text, out var grade));
        Assert.Equal((decimal)expected, grade);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("1.234,5")]
    public void An_unreadable_grade_is_rejected(string? text)
    {
        Assert.False(LocalNumber.TryParseGrade(text, out _));
    }

    [Theory]
    [InlineData("en", "1.3")]
    [InlineData("de", "1,3")]
    public void A_stored_grade_fills_the_input_in_the_ui_language(string language, string expected)
    {
        LocalDate.Language = language;

        Assert.Equal(expected, LocalNumber.GradeInput(1.3m));
        Assert.Equal("", LocalNumber.GradeInput(null));
    }

    [Theory]
    [InlineData("en", "e.g. 1.3")]
    [InlineData("de", "e.g. 1,3")]
    public void The_completion_modal_shows_the_grade_example_in_the_ui_language(string language, string expectedPlaceholder)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StudyLife.Client.Components.Setup.SetupCompletionModal>(p => p
            .Add(c => c.GradePlaceholder, "e.g. 1,3"));

        Assert.Equal(expectedPlaceholder, cut.Find("input").GetAttribute("placeholder"));
    }

    [Theory]
    [InlineData("en", "1,3")]
    [InlineData("en", "1.3")]
    [InlineData("de", "1,3")]
    [InlineData("de", "1.3")]
    public void The_completion_modal_reports_a_grade_typed_with_either_separator(string language, string typed)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();
        decimal? reported = null;

        var cut = ctx.Render<StudyLife.Client.Components.Setup.SetupCompletionModal>(p => p
            .Add(c => c.GradeDraftChanged, Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<decimal?>(this, v => reported = v)));
        cut.Find("input").Change(typed);

        Assert.Equal(1.3m, reported);
    }

    [Theory]
    [InlineData("en", "1.0", "5.0")]
    [InlineData("de", "1,0", "5,0")]
    public void The_hours_grade_scatter_axis_follows_the_ui_language(string language, string best, string worst)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsHoursGradeScatterCard>();

        var axis = cut.Find(".scatter-y-axis").TextContent;
        Assert.Contains(best, axis);
        Assert.Contains(worst, axis);
    }

    [Theory]
    [InlineData("en", "1.0–1.5", "> 4.0")]
    [InlineData("de", "1,0–1,5", "> 4,0")]
    public void The_grade_distribution_bands_follow_the_ui_language(string language, string first, string last)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsGradeDistributionCard>(p => p
            .Add(c => c.Buckets, new List<StatsGradeDistributionCard.GradeBucket>
            {
                new("1,0–1,5", 2, 100), new("> 4,0", 1, 50),
            }));

        var labels = cut.FindAll(".mini-bar-label").Select(e => e.TextContent).ToList();
        Assert.Equal(new[] { first, last }, labels);
        Assert.Contains(first, cut.Find(".mini-bar-col").GetAttribute("title"));
    }

    [Theory]
    [InlineData("en", "31.4", "Current pace: 11.1 h/week")]
    [InlineData("de", "31,4", "Aktuelles Tempo: 11,1 h/Woche")]
    public void The_graduation_goal_shows_required_hours_and_pace_in_the_ui_language(string language, string required, string pace)
    {
        // The store screenshots showed "Current pace: 11,1 h/week" on the English dashboard: the
        // server labels both values with a comma and only the required value was localized.
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StudyLife.Client.Components.Dashboard.DashboardGraduationGoalCard>(p => p
            .Add(c => c.RequiredValue, "31,4")
            .Add(c => c.PaceFormat, language == "en" ? "Current pace: {0} h/week" : "Aktuelles Tempo: {0} h/Woche")
            .Add(c => c.PaceValue, "11,1"));

        Assert.Equal(required, cut.Find(".stat-val").TextContent);
        Assert.Contains(pace, cut.Find(".quota-legend").TextContent);
    }

    [Theory]
    [InlineData("en", "Target: 12.5%", "Actual: 20%", "+7.5%")]
    [InlineData("de", "Target: 12,5%", "Actual: 20%", "+7,5%")]
    public void The_course_balance_percentages_follow_the_ui_language(string language, string target, string actual, string delta)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsCourseBalanceCard>(p => p
            .Add(c => c.TargetLabel, "Target")
            .Add(c => c.ActualLabel, "Actual")
            .Add(c => c.Rows, new List<StatsCourseBalanceCard.BalanceRow> { new("Maths", "∑", "#fff", 12.5, 20) }));

        var meta = cut.Find(".balance-meta").TextContent;
        Assert.Contains(target, meta);
        Assert.Contains(actual, meta);
        Assert.Contains(delta, cut.Find(".balance-delta").TextContent);
        // The bar geometry stays machine-formatted whatever the language.
        Assert.Contains("width: 20%", cut.Find(".balance-actual-fill").GetAttribute("style"));
    }

    [Theory]
    [InlineData("en", "42.5")]
    [InlineData("de", "42,5")]
    public async Task The_vo2max_values_follow_the_ui_language(string language, string expected)
    {
        LocalDate.Language = language;
        // The card's Apple Health caption loads its text table, which only implements IAsyncDisposable.
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddI18nText();

        var cut = ctx.Render<StatsCardioFitnessTrendCard>(p => p
            .Add(c => c.ValueFormat, "{0} ml/kg/min")
            .Add(c => c.Points, new List<StatsCardioFitnessTrendCard.CardioPoint>
            {
                new(new DateTime(2026, 9, 1), 41.25, 40.5), new(new DateTime(2026, 10, 1), 42.5, 60.25),
            }));

        Assert.Equal(expected, cut.FindAll(".mini-bar-value")[1].TextContent);
    }

    [Theory]
    [InlineData("en", "Avg. grade 1.66 · 1 course")]
    [InlineData("de", "Ø Note 1,66 · 1 Kurs")]
    public void The_grade_history_tooltip_shows_the_grade_in_the_ui_language_and_a_singular_course(string language, string expected)
    {
        LocalDate.Language = language;
        using var ctx = new BunitContext();

        var cut = ctx.Render<StatsGradeChartCard>(p => p
            .Add(c => c.TooltipFormat, PluralTests.ReadValue("StatsText", language, "GradeHistoryTooltip"))
            .Add(c => c.Points, new List<StatsGradeChartCard.GradePoint> { new("WS 25/26", 1.66m, 1, 85) }));

        Assert.EndsWith(expected, cut.Find(".mini-bar-col").GetAttribute("title"));
    }
}
