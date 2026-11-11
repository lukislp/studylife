// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;
using StudyLife.Server.Services;
using StudyLife.Server.Texts;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>The pure lookup <c>ServerText.Text</c>: fallback chain, null language, formatting in
/// the user's culture - against a hand-built catalogue, so the rule is tested without the data.</summary>
public class ServerTextLookupTests
{
    private static ServerTextCatalogue Catalogue() => new(new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["en"] = new Dictionary<string, string>
        {
            ["hello"] = "Hello {0}",
            ["only.en"] = "Only English",
            ["number"] = "{0:0.#} h",
            ["broken"] = "Broken {0} {1}",
        },
        ["de"] = new Dictionary<string, string>
        {
            ["hello"] = "Hallo {0}",
            ["number"] = "{0:0.#} h",
        },
        ["fr"] = new Dictionary<string, string> { ["hello"] = "Bonjour {0}" },
    });

    [Fact]
    public void Uses_the_language_of_the_user()
        => Assert.Equal("Bonjour Ada", Catalogue().Text("hello", "fr", "Ada"));

    [Fact]
    public void Region_tags_resolve_to_their_language()
        => Assert.Equal("Hallo Ada", Catalogue().Text("hello", "de-AT", "Ada"));

    [Fact]
    public void A_key_missing_in_the_language_falls_back_to_english()
        => Assert.Equal("Only English", Catalogue().Text("only.en", "de"));

    [Fact]
    public void A_key_missing_everywhere_comes_back_as_the_key()
        => Assert.Equal("nope.nothing", Catalogue().Text("nope.nothing", "de"));

    [Fact]
    public void An_unknown_language_is_the_legacy_default_german()
    {
        Assert.Equal("Hallo Ada", Catalogue().Text("hello", null, "Ada"));
        Assert.Equal("Hallo Ada", Catalogue().Text("hello", "", "Ada"));
        Assert.Equal("Hallo Ada", Catalogue().Text("hello", "xx", "Ada"));
    }

    [Fact]
    public void Numbers_are_formatted_in_the_culture_of_the_language()
    {
        // The decimal separator is the visible part of "the user's culture".
        Assert.Equal("1,5 h", Catalogue().Text("number", "de", 1.5));
        Assert.Equal("1.5 h", Catalogue().Text("number", "en", 1.5));
    }

    [Fact]
    public void A_template_that_does_not_fit_its_arguments_is_returned_as_is()
        => Assert.Equal("Broken {0} {1}", Catalogue().Text("broken", "en", "only one"));

    [Fact]
    public void The_bound_form_gives_the_same_answer()
    {
        var localizer = new ServerTextLocalizer(Catalogue(), "fr");
        Assert.Equal("Bonjour Ada", localizer.T("hello", "Ada"));
        Assert.Equal("fr", localizer.ResolvedLanguage);
        Assert.Equal("de", new ServerTextLocalizer(Catalogue(), null).ResolvedLanguage);
    }
}

/// <summary>The shipped catalogue: every supported language, every key, the same placeholders.
/// tools/check-i18n.py checks the same files in CI; this is the layer that also knows
/// <see cref="SupportedLanguages"/> and the keys the senders use.</summary>
public class ServerTextCatalogueTests
{
    private static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    private static IEnumerable<string> Placeholders(string template) =>
        Placeholder.Matches(template).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);

    [Fact]
    public void Has_a_table_for_exactly_the_supported_languages()
        => Assert.Equal(
            SupportedLanguages.Codes.OrderBy(c => c, StringComparer.Ordinal),
            ServerText.Default.Languages.OrderBy(c => c, StringComparer.Ordinal));

    [Fact]
    public void Every_language_has_exactly_the_keys_of_english()
    {
        var english = ServerText.Default.Keys("en").ToHashSet();
        Assert.NotEmpty(english);
        foreach (var language in SupportedLanguages.Codes)
            Assert.True(english.SetEquals(ServerText.Default.Keys(language)), $"{language}: key set differs from en");
    }

    [Fact]
    public void Every_translation_keeps_the_placeholders_of_english()
    {
        foreach (var key in ServerText.Default.Keys("en"))
        {
            var expected = Placeholders(ServerText.Default.Template(key, "en")!);
            foreach (var language in SupportedLanguages.Codes)
            {
                var template = ServerText.Default.Template(key, language)!;
                Assert.False(string.IsNullOrWhiteSpace(template), $"{language}/{key}: empty");
                Assert.True(expected.SequenceEqual(Placeholders(template)), $"{language}/{key}: placeholders differ from en");
            }
        }
    }

    [Fact]
    public void Every_motivation_style_has_a_complete_quote_pool()
    {
        foreach (var style in MotivationalStyles.All)
            for (var n = 1; n <= BackgroundTaskService.QuotesPerStyle; n++)
                Assert.NotNull(ServerText.Default.Template($"motivation.{style}.{n}", "en"));
    }

    [Fact]
    public void No_template_formats_badly_with_plausible_arguments()
    {
        // Format every template of every language with an argument of the type its specifier
        // asks for: a malformed brace or a specifier that throws in some culture would otherwise
        // only show up when that user's notification fires.
        foreach (var language in SupportedLanguages.Codes)
            foreach (var key in ServerText.Default.Keys(language))
            {
                var template = ServerText.Default.Template(key, language)!;
                var holes = Placeholder.Matches(template).Select(m => m.Value.Trim('{', '}').Split(':', 2)).ToList();
                if (holes.Count == 0) continue;
                var args = new object[holes.Max(h => int.Parse(h[0])) + 1];
                foreach (var hole in holes)
                {
                    var spec = hole.Length > 1 ? hole[1] : "";
                    args[int.Parse(hole[0])] = spec switch
                    {
                        "t" => new TimeOnly(14, 30),
                        "d" => new DateTime(2026, 7, 15),
                        "" => "Course",
                        _ => 3.5,
                    };
                }
                var text = string.Format(ServerTextCatalogue.CultureFor(language), template, args);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}/{key}");
            }
    }

    [Fact]
    public void German_is_what_a_user_without_a_reported_language_gets()
        => Assert.Equal("Willkommen zurück 👋", ServerText.Text("comeback.title", null));

    [Fact]
    public void Dates_and_times_follow_the_culture_of_the_user()
    {
        var goalDe = ServerText.Text("goal.body", "de", "Analysis", new DateTime(2026, 7, 15));
        var goalEn = ServerText.Text("goal.body", "en", "Analysis", new DateTime(2026, 7, 15));
        Assert.Contains("15.07.2026", goalDe);
        Assert.Contains("7/15/2026", goalEn);
    }
}

/// <summary>The senders' pure text builders in more than one language (the end-to-end push
/// payload is encrypted and never visible, see BackgroundTaskServiceSessionReminderResourceLinkTests).</summary>
public class NotificationTextBuilderTests
{
    [Fact]
    public void Session_reminder_body_is_in_the_language_of_the_user()
    {
        var start = new DateTime(2026, 7, 15, 14, 30, 0);
        // English writes the time as 2:30 PM (the separator before PM varies with the ICU version).
        Assert.StartsWith("Analysis 1: Integrals at 2:30", BackgroundTaskService.BuildSessionReminderBody("Analysis 1", "Integrals", start, 0, "en"));
        Assert.Equal("Analysis 1 um 14:30 Uhr — Ressourcen für diesen Kurs hinterlegt: 2", BackgroundTaskService.BuildSessionReminderBody("Analysis 1", null, start, 2, null));
    }

    [Theory]
    [InlineData(60, "en", "Study session in 1 hour 📚")]
    [InlineData(1, "en", "Study session in 1 minute! 🔔")]
    [InlineData(30, "en", "Study session in 30 minutes ⏰")]
    [InlineData(90, "de", "Lernphase in 90 Minuten ⏰")]
    [InlineData(60, null, "Lernphase in 1 Stunde 📚")]
    public void Session_reminder_title_follows_threshold_and_language(int minutes, string? language, string expected)
        => Assert.Equal(expected, BackgroundTaskService.BuildSessionReminderTitle(minutes, language));

    [Fact]
    public void Phase_alert_is_localised()
    {
        var alert = BackgroundTaskService.BuildPhaseChangeAlert("Pomodoro", isBreak: true, round: 2, totalRounds: 4, phaseMinutes: 5, language: "en");
        Assert.Equal("Time for a break", alert.Title);
        Assert.Equal("Pomodoro: round 2 of 4 done – 5 min break.", alert.Body);
    }

    [Fact]
    public void Daily_quote_comes_from_the_catalogue_in_the_users_language_and_picks_the_same_index()
    {
        var date = new DateTime(2026, 7, 15);
        var german = BackgroundTaskService.PickDailyMotivationQuote("zen", date, "de");
        var english = BackgroundTaskService.PickDailyMotivationQuote("zen", date, "en");
        Assert.NotEqual(german, english);
        // The unknown style still falls back to the default style, in the requested language.
        Assert.Equal(BackgroundTaskService.PickDailyMotivationQuote(MotivationalStyles.Default, date, "fr"),
            BackgroundTaskService.PickDailyMotivationQuote("nope", date, "fr"));
    }
}
