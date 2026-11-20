// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;

namespace StudyLife.Client.Services;

/// <summary>
/// Plural agreement for UI texts ("1 day", "2 days", "5 dni", "21 день"). The i18n tables are flat
/// key -> string, so a plural text keeps its single key and carries its variants in the value:
/// <c>one=Due in {0} day|other=Due in {0} days</c>. The variant names are the CLDR plural
/// categories (zero, one, two, few, many, other); only the ones a language distinguishes are
/// written, and <c>other</c> is the fallback. A value without variants is used as it is (languages
/// whose noun does not change with the number, or abbreviations such as "d.").
///
/// Like <see cref="LocalNumber"/> it follows <see cref="LocalDate.Language"/> and needs no ICU.
/// Counts are whole numbers; the rules below are the CLDR integer rules of the supported languages.
/// </summary>
public static class Plural
{
    private static readonly string[] Categories = ["zero", "one", "two", "few", "many", "other"];

    /// <summary>Formats a plural template for the current UI language; {0} is the count.</summary>
    public static string Format(string? template, long count) => Format(template, LocalDate.Language, count);

    public static string Format(string? template, string language, long count)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var variants = Parse(template);
        var text = template;
        if (variants is not null)
        {
            var category = Category(language, count);
            text = variants.TryGetValue(category, out var picked) ? picked
                : variants.TryGetValue("other", out var other) ? other
                : variants.Values.Last();
        }
        return string.Format(CultureInfo.InvariantCulture, text, count);
    }

    /// <summary>The variants of a plural template, or null when the value is a plain text.</summary>
    public static IReadOnlyDictionary<string, string>? Parse(string template)
    {
        var parts = template.Split('|');
        var result = new Dictionary<string, string>();
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || Array.IndexOf(Categories, part[..eq]) < 0) return null;
            result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    /// <summary>The CLDR plural categories a language uses for whole numbers.</summary>
    public static IReadOnlyList<string> CategoriesOf(string language) => TwoLetter(language) switch
    {
        "cs" or "sk" or "uk" or "ru" or "hr" or "lt" or "ro" => ["one", "few", "other"],
        "pl" => ["one", "few", "many", "other"],
        "sl" => ["one", "two", "few", "other"],
        "lv" => ["zero", "one", "other"],
        "mt" => ["one", "few", "many", "other"],
        "ga" => ["one", "two", "few", "many", "other"],
        _ => ["one", "other"],
    };

    public static string Category(string language, long count)
    {
        var n = Math.Abs(count);
        long m10 = n % 10, m100 = n % 100;
        return TwoLetter(language) switch
        {
            "fr" or "pt" => n <= 1 ? "one" : "other",
            "cs" or "sk" => n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other",
            "pl" => n == 1 ? "one"
                : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few" : "many",
            "ru" or "uk" => m10 == 1 && m100 != 11 ? "one"
                : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few" : "many",
            "hr" => m10 == 1 && m100 != 11 ? "one"
                : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few" : "other",
            "sl" => m100 == 1 ? "one" : m100 == 2 ? "two" : m100 is 3 or 4 ? "few" : "other",
            "lt" => m10 == 1 && m100 is < 11 or > 19 ? "one"
                : m10 is >= 2 and <= 9 && m100 is < 11 or > 19 ? "few" : "other",
            "lv" => m10 == 0 || m100 is >= 11 and <= 19 ? "zero" : m10 == 1 && m100 != 11 ? "one" : "other",
            "ro" => n == 1 ? "one" : n == 0 || m100 is >= 2 and <= 19 ? "few" : "other",
            "mt" => n == 1 ? "one" : n == 0 || m100 is >= 2 and <= 10 ? "few" : m100 is >= 11 and <= 19 ? "many" : "other",
            "ga" => n == 1 ? "one" : n == 2 ? "two" : n is >= 3 and <= 6 ? "few" : n is >= 7 and <= 10 ? "many" : "other",
            _ => n == 1 ? "one" : "other", // en, de, nl, sv, da, fi, et, el, hu, bg, es, it
        };
    }

    private static string TwoLetter(string language) =>
        (language.Length >= 2 ? language[..2] : language).ToLowerInvariant();
}
