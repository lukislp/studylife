namespace StudyLife.Client.Services;

/// <summary>
/// Short date formatting that follows the UI language. The client runs with
/// InvariantGlobalization (no ICU payload, see StudyLife.Client.csproj), so every culture
/// formats like the invariant one - DateTime.ToString("d") always produced US-style
/// 08/30/2026 regardless of the selected language (found live on the passkey and add-on lists).
/// A small per-language pattern table is the whole fix; ISO-ish languages get their native order.
/// The active language is pushed in by MainLayout/Program.cs whenever I18nText resolves or
/// changes it - a static because dates are formatted from many components and the value is
/// per app instance anyway (one language per browser tab).
/// </summary>
public static class LocalDate
{
    public static string Language { get; set; } = "de";

    public static string Short(DateTime value) => value.ToString(PatternFor(Language), System.Globalization.CultureInfo.InvariantCulture);

    private static string PatternFor(string language) => (language.Length >= 2 ? language[..2].ToLowerInvariant() : language) switch
    {
        "en" or "ga" or "mt" or "es" or "fr" or "it" or "pt" or "el" => "dd/MM/yyyy",
        "nl" => "dd-MM-yyyy",
        "sv" or "lt" => "yyyy-MM-dd",
        "hu" => "yyyy. MM. dd.",
        _ => "dd.MM.yyyy", // de, cs, sk, pl, da, fi, et, lv, ro, bg, hr, sl, ru, uk
    };
}
