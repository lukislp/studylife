// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>One UI language of the app: the i18ntext table suffix, the flag and the native name
/// shown in the language popup.</summary>
public sealed record UiLanguageInfo(string Code, string Flag, string Name);

/// <summary>
/// The single registry of the UI languages and the single rule that decides which one a start
/// uses. Pure on purpose (no JS, no DI, no I/O): <see cref="UiLanguageStartup"/> gathers the
/// inputs, the web client and the native app shell both call it, and everything that wants the
/// list of languages (the popup in MainLayout, the matching below) reads <see cref="All"/>.
/// A test keeps <see cref="All"/> in step with the language files under i18ntext/.
/// </summary>
public static class UiLanguage
{
    /// <summary>The language of a start that has no stored choice and no usable device language:
    /// the neutral one, not the maintainer's own.</summary>
    public const string Fallback = "en";

    /// <summary>What an existing user who never stored a choice has been seeing: before the
    /// device language was honoured, the first start forced German. See <see cref="Resolve"/>.</summary>
    public const string LegacyDefault = "de";

    /// <summary>The supported languages in popup order (English first).</summary>
    public static IReadOnlyList<UiLanguageInfo> All { get; } = new UiLanguageInfo[]
    {
        new("en", "🇬🇧", "English"),
        new("de", "🇩🇪", "Deutsch"),
        new("fr", "🇫🇷", "Français"),
        new("es", "🇪🇸", "Español"),
        new("it", "🇮🇹", "Italiano"),
        new("pt", "🇵🇹", "Português"),
        new("nl", "🇳🇱", "Nederlands"),
        new("da", "🇩🇰", "Dansk"),
        new("sv", "🇸🇪", "Svenska"),
        new("fi", "🇫🇮", "Suomi"),
        new("el", "🇬🇷", "Ελληνικά"),
        new("pl", "🇵🇱", "Polski"),
        new("cs", "🇨🇿", "Čeština"),
        new("sk", "🇸🇰", "Slovenčina"),
        new("hu", "🇭🇺", "Magyar"),
        new("ro", "🇷🇴", "Română"),
        new("bg", "🇧🇬", "Български"),
        new("hr", "🇭🇷", "Hrvatski"),
        new("sl", "🇸🇮", "Slovenščina"),
        new("et", "🇪🇪", "Eesti"),
        new("lv", "🇱🇻", "Latviešu"),
        new("lt", "🇱🇹", "Lietuvių"),
        new("mt", "🇲🇹", "Malti"),
        new("ga", "🇮🇪", "Gaeilge"),
        new("uk", "🇺🇦", "Українська"),
        new("ru", "🇷🇺", "Русский"),
    };

    /// <summary>
    /// Matches one BCP 47 language tag against the supported languages: the whole tag first, then
    /// its primary language subtag (<c>pt-BR</c> and <c>pt_PT</c> give <c>pt</c>, <c>de-AT</c> gives
    /// <c>de</c>). Returns null when the language is not supported (Norwegian, Chinese, ...), so a
    /// caller can go on with the next preference. None of the tables distinguishes regions or
    /// scripts, so there is nothing finer to match on.
    /// </summary>
    public static string? Match(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var normalized = tag.Trim().Replace('_', '-').ToLowerInvariant();
        if (Find(normalized) is { } exact) return exact;
        var dash = normalized.IndexOf('-');
        return dash > 0 ? Find(normalized[..dash]) : null;
    }

    /// <summary>
    /// The language a start uses:
    /// <list type="number">
    /// <item>the user's stored choice, if there is one (a region tag such as <c>en-GB</c> that an
    /// older library version persisted counts as its language);</item>
    /// <item>an existing user without a stored choice (<paramref name="hasPriorUse"/>: a session
    /// token is already on this device) keeps <see cref="LegacyDefault"/>, which is what the app
    /// has always shown them;</item>
    /// <item>the best match of <paramref name="deviceLanguages"/>, in the order of preference;</item>
    /// <item><see cref="Fallback"/>.</item>
    /// </list>
    /// </summary>
    public static string Resolve(string? stored, IEnumerable<string>? deviceLanguages, bool hasPriorUse = false)
    {
        if (Match(stored) is { } chosen) return chosen;
        if (hasPriorUse) return LegacyDefault;
        if (deviceLanguages is not null)
        {
            foreach (var tag in deviceLanguages)
            {
                if (Match(tag) is { } best) return best;
            }
        }
        return Fallback;
    }

    private static string? Find(string code)
    {
        foreach (var language in All)
        {
            if (language.Code == code) return language.Code;
        }
        return null;
    }
}
