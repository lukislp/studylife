using System.Globalization;

namespace StudyLife.Client.Services;

/// <summary>
/// Localized weekday and month names for ONE language, as produced by
/// <c>Intl.DateTimeFormat</c> in the browser (wwwroot/js/localdate.js, loaded by
/// <see cref="LocalDateNames"/>). Weekday arrays are indexed by <see cref="DayOfWeek"/>
/// (Sunday = 0), month arrays by month number - 1.
/// </summary>
public sealed record DateNames(
    string[] WeekdaysShort,
    string[] WeekdaysLong,
    string[] MonthsShort,
    string[] MonthsLong);

/// <summary>
/// Date/time formatting that follows the UI language. The client runs with
/// InvariantGlobalization (no ICU payload, see StudyLife.Client.csproj), so every culture
/// formats like the invariant one - DateTime.ToString("d") always produced US-style
/// 08/30/2026 and ToString("ddd") always produced English "Thu", regardless of the selected
/// language. Numbers come from a small per-language pattern table (ISO-ish languages get their
/// native order); the weekday/month NAMES come from the browser's own Intl data, which covers
/// all 26 languages without shipping ICU or hand-writing 26 name tables.
///
/// The active language is pushed in by Program.cs/MainLayout whenever I18nText resolves or
/// changes it - a static because dates are formatted from many components and the value is
/// per app instance anyway (one language per browser tab). Until the names have been loaded,
/// every helper falls back to the invariant English names, so nothing here can be null or
/// throw during the synchronous pre-init render.
/// </summary>
public static class LocalDate
{
    private static readonly DateNames Invariant = new(
        ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"],
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]);

    private static DateNames _names = Invariant;

    public static string Language { get; set; } = "de";

    /// <summary>Raised after <see cref="ApplyNames"/> actually swapped in a different name set,
    /// so the shell can re-render pages that already formatted dates with the old names.</summary>
    public static event Action? NamesChanged;

    /// <summary>
    /// Installs the names for <paramref name="language"/>. Values crossing the JS boundary are
    /// validated here (a locale the browser doesn't know can yield short arrays); anything
    /// malformed keeps the invariant fallback rather than risking an IndexOutOfRange in markup.
    /// </summary>
    public static void ApplyNames(string language, DateNames? names)
    {
        var next = IsComplete(names) ? names! : Invariant;
        Language = language;
        if (ReferenceEquals(_names, next)) return;
        _names = next;
        NamesChanged?.Invoke();
    }

    /// <summary>Resets to the invariant names - used by tests, and by a failed load.</summary>
    public static void ResetNames() => ApplyNames(Language, null);

    private static bool IsComplete(DateNames? n) =>
        n is not null
        && n.WeekdaysShort.Length == 7 && n.WeekdaysLong.Length == 7
        && n.MonthsShort.Length == 12 && n.MonthsLong.Length == 12
        && n.WeekdaysShort.All(s => !string.IsNullOrWhiteSpace(s))
        && n.WeekdaysLong.All(s => !string.IsNullOrWhiteSpace(s))
        && n.MonthsShort.All(s => !string.IsNullOrWhiteSpace(s))
        && n.MonthsLong.All(s => !string.IsNullOrWhiteSpace(s));

    // ── Numeric dates ────────────────────────────────────────────────────────

    /// <summary>Full numeric date, e.g. 30.08.2026 / 30/08/2026 / 2026-08-30.</summary>
    public static string Short(DateTime value) => value.ToString(PatternFor(Language), CultureInfo.InvariantCulture);

    /// <summary>Full numeric date with a two-digit year - for tight chart axis labels.</summary>
    public static string ShortYear(DateTime value) => value.ToString(PatternFor(Language).Replace("yyyy", "yy"), CultureInfo.InvariantCulture);

    /// <summary>Day and month without the year, e.g. 30.08. / 30/08 / 08-30.</summary>
    public static string DayMonth(DateTime value) => value.ToString(DayMonthPatternFor(Language), CultureInfo.InvariantCulture);

    /// <summary>Clock time: 24h everywhere except English, which expects 12h + AM/PM.</summary>
    public static string Time(DateTime value) => IsEnglish(Language)
        ? value.ToString("h:mm tt", CultureInfo.InvariantCulture)
        : value.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Full numeric date plus clock time.</summary>
    public static string DateAndTime(DateTime value) => $"{Short(value)} {Time(value)}";

    // ── Localized names ──────────────────────────────────────────────────────

    public static string Weekday(DateTime value, bool @short) => Weekday(value.DayOfWeek, @short);

    public static string Weekday(DayOfWeek day, bool @short) =>
        (@short ? _names.WeekdaysShort : _names.WeekdaysLong)[(int)day];

    public static string MonthName(DateTime value, bool @short) => MonthName(value.Month, @short);

    /// <param name="month">1-12.</param>
    public static string MonthName(int month, bool @short) =>
        (@short ? _names.MonthsShort : _names.MonthsLong)[month - 1];

    // ── Patterns ─────────────────────────────────────────────────────────────

    private static bool IsEnglish(string language) => TwoLetter(language) == "en";

    private static string TwoLetter(string language) =>
        (language.Length >= 2 ? language[..2] : language).ToLowerInvariant();

    private static string PatternFor(string language) => TwoLetter(language) switch
    {
        "en" or "ga" or "mt" or "es" or "fr" or "it" or "pt" or "el" => "dd/MM/yyyy",
        "nl" => "dd-MM-yyyy",
        "sv" or "lt" => "yyyy-MM-dd",
        "hu" => "yyyy. MM. dd.",
        _ => "dd.MM.yyyy", // de, cs, sk, pl, da, fi, et, lv, ro, bg, hr, sl, ru, uk
    };

    private static string DayMonthPatternFor(string language) => TwoLetter(language) switch
    {
        "en" or "ga" or "mt" or "es" or "fr" or "it" or "pt" or "el" => "dd/MM",
        "nl" => "dd-MM",
        "sv" or "lt" => "MM-dd",
        "hu" => "MM. dd.",
        _ => "dd.MM.",
    };
}
