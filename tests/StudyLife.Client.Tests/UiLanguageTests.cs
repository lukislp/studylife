// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

public class UiLanguageTests
{
    private static readonly string[] German = { "de-DE", "en-US" };

    [Theory]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("DE", "de")]
    [InlineData(" fr ", "fr")]
    public void Exact_tag_matches(string tag, string expected)
        => Assert.Equal(expected, UiLanguage.Match(tag));

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("pt_PT", "pt")]
    [InlineData("de-AT", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("ga-IE", "ga")]
    [InlineData("el-GR", "el")]
    [InlineData("uk-UA", "uk")]
    public void Region_tag_matches_its_language(string tag, string expected)
        => Assert.Equal(expected, UiLanguage.Match(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("no")]
    [InlineData("nb-NO")]
    [InlineData("zh-Hans")]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    [InlineData("-de")]
    [InlineData("klingon")]
    public void Unsupported_or_malformed_tag_does_not_match(string? tag)
        => Assert.Null(UiLanguage.Match(tag));

    [Fact]
    public void First_start_follows_the_device_language()
        => Assert.Equal("de", UiLanguage.Resolve(null, German));

    [Fact]
    public void First_start_in_english_is_english()
        => Assert.Equal("en", UiLanguage.Resolve(null, new[] { "en-US" }));

    [Fact]
    public void Device_preference_order_wins_over_later_entries()
        => Assert.Equal("fr", UiLanguage.Resolve(null, new[] { "fr-CA", "de", "en" }));

    [Fact]
    public void Unsupported_languages_are_skipped_in_preference_order()
        => Assert.Equal("pl", UiLanguage.Resolve(null, new[] { "zh-Hans-CN", "nb", "pl-PL", "en" }));

    [Fact]
    public void Language_only_match_is_used_when_the_region_is_unknown()
        => Assert.Equal("pt", UiLanguage.Resolve(null, new[] { "pt-BR" }));

    [Theory]
    [InlineData("ja-JP", "zh-CN")]
    [InlineData]
    public void No_supported_device_language_gives_english_not_german(params string[] device)
        => Assert.Equal("en", UiLanguage.Resolve(null, device));

    [Fact]
    public void Missing_device_languages_give_english()
        => Assert.Equal(UiLanguage.Fallback, UiLanguage.Resolve(null, null));

    [Theory]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("fr", "fr")]
    [InlineData("en-GB", "en")]
    public void Stored_choice_wins_over_device_and_prior_use(string stored, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(stored, new[] { "ru-RU", "pl" }));
        Assert.Equal(expected, UiLanguage.Resolve(stored, new[] { "ru-RU" }, hasPriorUse: true));
    }

    [Fact]
    public void Stored_german_stays_german_on_an_english_device()
        => Assert.Equal("de", UiLanguage.Resolve("de", new[] { "en-US" }, hasPriorUse: true));

    [Fact]
    public void Existing_user_without_a_stored_choice_keeps_german()
        => Assert.Equal("de", UiLanguage.Resolve(null, new[] { "en-US" }, hasPriorUse: true));

    [Fact]
    public void Unusable_stored_value_counts_as_no_choice()
    {
        Assert.Equal("en", UiLanguage.Resolve("xx", new[] { "en-US" }));
        Assert.Equal("de", UiLanguage.Resolve("xx", new[] { "en-US" }, hasPriorUse: true));
    }

    [Fact]
    public void Registry_has_unique_codes_and_english_first()
    {
        var codes = UiLanguage.All.Select(l => l.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(UiLanguage.Fallback, codes[0]);
        Assert.Contains(UiLanguage.LegacyDefault, codes);
        Assert.All(UiLanguage.All, l => Assert.False(string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.Flag)));
    }

    [Fact]
    public void Registry_is_the_shared_language_list_with_display_data_added()
    {
        // One list of language codes (StudyLife.Shared.SupportedLanguages, also what the server
        // validates against); UiLanguage only adds flag and native name, same codes, same order.
        Assert.Equal(SupportedLanguages.Codes, UiLanguage.All.Select(l => l.Code).ToList());
        Assert.Equal(SupportedLanguages.Fallback, UiLanguage.Fallback);
        Assert.Equal(SupportedLanguages.LegacyDefault, UiLanguage.LegacyDefault);
    }

    [Fact]
    public void Registry_matches_the_language_files_of_every_i18n_table()
    {
        var dir = FindI18nDirectory();
        var registry = UiLanguage.All.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var byTable = Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(name => (Table: name[..name.LastIndexOf('.')], Lang: name[(name.LastIndexOf('.') + 1)..]))
            .GroupBy(x => x.Table)
            .ToList();

        Assert.NotEmpty(byTable);
        foreach (var table in byTable)
        {
            var languages = table.Select(x => x.Lang).OrderBy(c => c, StringComparer.Ordinal).ToList();
            Assert.True(registry.SequenceEqual(languages),
                $"{table.Key}: files {string.Join(",", languages)} differ from the registry {string.Join(",", registry)}");
        }
    }

    private static string FindI18nDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "StudyLife.Client", "i18ntext");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/StudyLife.Client/i18ntext not found above " + AppContext.BaseDirectory);
    }
}
