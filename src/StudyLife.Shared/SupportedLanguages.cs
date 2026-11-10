// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// The one list of UI languages and the one tag-matching rule, shared by the client (language
/// picker, start-up rule: <c>UiLanguage</c> keeps the display data - flag and native name - on
/// top of this list) and the server (validation of the per-user language setting, lookup in the
/// server text catalogue). A client test keeps this list in step with the i18ntext tables, a
/// server test keeps it in step with the server text catalogue, and tools/check-i18n.py checks
/// the files of both, so a language cannot be added in one place only.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>The language of a start that has no stored choice and no usable device language:
    /// the neutral one, not the maintainer's own. Also the second step of every text lookup
    /// (language, then this, then the key itself).</summary>
    public const string Fallback = "en";

    /// <summary>What the product has always shown and sent when it does not know better: the
    /// first start used to force German, and the server only ever sent German. An existing user
    /// whose language the server has not heard yet therefore keeps getting German until their
    /// client reports its language.</summary>
    public const string LegacyDefault = "de";

    /// <summary>The supported language codes in picker order (English first).</summary>
    public static IReadOnlyList<string> Codes { get; } = new[]
    {
        "en", "de", "fr", "es", "it", "pt", "nl", "da", "sv", "fi", "el", "pl", "cs", "sk", "hu",
        "ro", "bg", "hr", "sl", "et", "lv", "lt", "mt", "ga", "uk", "ru",
    };

    /// <summary>True for exactly one of <see cref="Codes"/> (lower case, no region); the check
    /// behind the 400 of the language endpoint.</summary>
    public static bool IsSupported(string? code) => code is not null && Find(code) is not null;

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

    private static string? Find(string code)
    {
        foreach (var known in Codes)
        {
            if (known == code) return known;
        }
        return null;
    }
}
