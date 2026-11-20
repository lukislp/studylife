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
}