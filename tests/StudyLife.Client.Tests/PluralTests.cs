// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>Counted nouns agree with the number in the UI language: "1 day", "2 days" (the dashboard
/// chip used to say "Due in 24 day(s)"), and Slavic or Baltic forms where one noun is not enough.</summary>
public class PluralTests
{
    /// <summary>The counted texts beyond days: achievement milestones ("1 courses completed" on the
    /// review account), the former "(s)" texts and every other "N noun" label that can show a 1.
    /// {0} is always the count; a second number is passed through the args overload of Plural.Format.
    /// Declared before <see cref="PluralKeys"/>, which includes it (static fields initialise in order).</summary>
    internal static readonly (string Table, string Key)[] CountedTexts =
    [
        ("IndexText", "AchievementSessionsName"), ("IndexText", "AchievementCoursesName"),
        ("IndexText", "AchievementProgramsName"), ("IndexText", "AchievementEarlyBirdName"),
        ("IndexText", "AchievementNightOwlName"), ("IndexText", "AchievementWeekendWarriorName"),
        ("IndexText", "AchievementPerfectWeekName"), ("IndexText", "AchievementNotesName"),
        ("IndexText", "AchievementCourseDiversityName"), ("IndexText", "InactivityNudgeText"),
        ("IndexText", "FocusScoreRatioFormat"), ("AccountDeletionText", "OwnedAppsText"),
        ("AuthText", "LinkNewDeviceExpiresFormat"), ("CalendarText", "ImportSummary"),
        ("MainLayoutText", "PushDevicesRegisteredDaysAgo"), ("MainLayoutText", "PendingSyncTooltip"),
        ("PlannerText", "ProposalHeading"), ("PlannerText", "WeekPlanHeading"),
        ("PlannerText", "ExamPlanAccepted"), ("PlannerText", "WeekPlanAccepted"),
        ("SetupText", "ProgramSummaryLabel"), ("StatsText", "GradeHistoryTooltip"),
        ("StatsText", "SessionCountFormat"), ("StatsText", "GradeDistributionCourseFormat"),
        ("StatsText", "DonutDetailMoreFormat"),
    ];

    private static readonly (string Table, string Key)[] PluralKeys =
    [
        .. CountedTexts,
        ("IndexText", "InDays"), ("IndexText", "OverdueByDays"), ("IndexText", "CourseDeadlineInDaysFormat"),
        ("IndexText", "LastStudiedDaysAgo"), ("NotesText", "DayAgo"), ("ReportText", "StatusOverdueFormat"),
        ("ReportText", "StatusDueFormat"), ("SetupText", "ReminderDaysFormat"),
        ("StatsText", "DueInDays"), ("StatsText", "OverdueByDays"), ("PlannerText", "LastStudiedDaysAgo"), ("PlannerText", "ExamInDays"),
    ];

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 24, "other")]
    [InlineData("en", 0, "other")]
    [InlineData("de", 1, "one")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 2, "other")]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 2, "few")]
    [InlineData("pl", 5, "many")]
    [InlineData("pl", 12, "many")]
    [InlineData("pl", 22, "few")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 11, "many")]
    [InlineData("uk", 3, "few")]
    [InlineData("cs", 3, "few")]
    [InlineData("cs", 5, "other")]
    [InlineData("sl", 2, "two")]
    [InlineData("sl", 101, "one")]
    [InlineData("lt", 21, "one")]
    [InlineData("lt", 12, "other")]
    [InlineData("lv", 10, "zero")]
    [InlineData("lv", 21, "one")]
    [InlineData("ro", 0, "few")]
    [InlineData("ro", 20, "other")]
    [InlineData("hr", 22, "few")]
    [InlineData("mt", 11, "many")]
    [InlineData("ga", 7, "many")]
    public void Counts_fall_into_the_cldr_category_of_the_language(string language, long count, string expected) =>
        Assert.Equal(expected, Plural.Category(language, count));

    [Fact]
    public void A_template_picks_the_variant_of_the_count()
    {
        const string template = "one=Due in {0} day|other=Due in {0} days";

        Assert.Equal("Due in 1 day", Plural.Format(template, "en", 1));
        Assert.Equal("Due in 24 days", Plural.Format(template, "en", 24));
    }

    [Fact]
    public void A_missing_variant_falls_back_to_other()
    {
        const string template = "one=za {0} dzień|other=za {0} dni";

        Assert.Equal("za 3 dni", Plural.Format(template, "pl", 3)); // "few" is not written out: it reads as other
    }

    [Fact]
    public void A_text_without_variants_is_used_as_it_is()
    {
        Assert.Equal("za 4 dn.", Plural.Format("za {0} dn.", "ru", 4));
        Assert.Equal("", Plural.Format(null, "en", 4));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_plural_text_of_a_language_is_well_formed(string language)
    {
        foreach (var (table, key) in PluralKeys)
        {
            var value = ReadValue(table, language, key);
            Assert.Contains("{0}", value);
            var variants = Plural.Parse(value);
            if (variants is null) continue; // plain text: the noun does not change, or an abbreviation

            Assert.True(variants.ContainsKey("other"), $"{table}.{language}.{key} lacks 'other'");
            var allowed = Plural.CategoriesOf(language);
            foreach (var category in variants.Keys)
                Assert.True(allowed.Contains(category), $"{table}.{language}.{key}: '{category}' is not a plural category of {language}");
            Assert.All(variants.Values, v => Assert.Contains("{0}", v));
            Assert.DoesNotContain("(s)", value);
        }
    }

    [Fact]
    public void The_english_day_texts_no_longer_use_the_noun_in_brackets()
    {
        foreach (var (table, key) in PluralKeys)
        {
            var value = ReadValue(table, "en", key);
            Assert.NotNull(Plural.Parse(value));
            Assert.DoesNotContain("(s)", value);
        }
    }

    public static IEnumerable<object[]> AllLanguages() =>
        Directory.GetFiles(I18nDirectory(), "IndexText.*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('.')[1])
            .OrderBy(l => l)
            .Select(l => new object[] { l });

    [Fact]
    public void A_template_with_a_second_number_keeps_the_count_as_zero_and_fills_the_rest_in_order()
    {
        const string template = "one={0} session · {1} h total|other={0} sessions · {1} h total";

        Assert.Equal("1 session · 1.5 h total", Plural.Format(template, "en", 1, "1.5"));
        Assert.Equal("3 sessions · 4.5 h total", Plural.Format(template, "en", 3, "4.5"));
        Assert.Equal("Avg. grade 1,7 · 2 courses", Plural.Format("Avg. grade {1} · {0} courses", "de", 2, "1,7"));
        Assert.Equal("", Plural.Format(null, "en", 1, "x"));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_counted_text_formats_for_one_and_for_many_without_error(string language)
    {
        foreach (var (table, key) in CountedTexts)
        {
            var template = ReadValue(table, language, key);
            foreach (var count in new long[] { 0, 1, 2, 5, 11, 21, 101 })
            {
                var text = Plural.Format(template, language, count, "X");
                Assert.Contains(count.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
                Assert.DoesNotContain("{", text);
            }
        }
    }

    [Fact]
    public void No_english_text_counts_a_noun_in_brackets()
    {
        var offenders = Directory.GetFiles(I18nDirectory(), "*.en.json")
            .SelectMany(f =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true });
                return doc.RootElement.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Contains("(s)"))
                    .Select(p => $"{Path.GetFileName(f)}:{p.Name}")
                    .ToList();
            })
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("en", "AchievementCoursesName", "1 course completed", "10 courses completed")]
    [InlineData("en", "AchievementPerfectWeekName", "1 perfect week", "4 perfect weeks")]
    [InlineData("en", "AchievementProgramsName", "1 degree program completed", "2 degree programs completed")]
    [InlineData("de", "AchievementCoursesName", "1 Kurs abgeschlossen", "10 Kurse abgeschlossen")]
    [InlineData("de", "AchievementPerfectWeekName", "1 perfekte Woche", "4 perfekte Wochen")]
    [InlineData("de", "AchievementProgramsName", "1 Studiengang abgeschlossen", "2 Studiengänge abgeschlossen")]
    [InlineData("pl", "AchievementCoursesName", "1 ukończony kurs", "10 ukończonych kursów")]
    public void Achievement_milestones_of_one_use_the_singular(string language, string key, string one, string many)
    {
        var template = ReadValue("IndexText", language, key);
        var manyCount = long.Parse(many.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(one, Plural.Format(template, language, 1));
        Assert.Equal(many, Plural.Format(template, language, manyCount));
    }

    internal static string ReadValue(string table, string language, string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(I18nDirectory(), $"{table}.{language}.json")),
            new JsonDocumentOptions { AllowTrailingCommas = true });
        return doc.RootElement.GetProperty(key).GetString()!;
    }

    internal static string I18nDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "StudyLife.Client", "i18ntext");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/StudyLife.Client/i18ntext not found above " + AppContext.BaseDirectory);
    }
}
