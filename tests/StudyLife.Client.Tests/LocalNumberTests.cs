// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;

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
}
