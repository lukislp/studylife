namespace StudyLife.Server.Services;

/// <summary>
/// Parses the comma-separated reminder thresholds configurable in setup from
/// UserSettingsEntity. Falls back to the previous hardcoded values if
/// the field is empty, invalid, or (for existing installations) not yet migrated.
/// </summary>
public static class ReminderSettings
{
    public static readonly int[] DefaultSessionReminderMinutes = [60, 30, 10, 5, 3, 2, 1];
    public static readonly int[] DefaultCourseGoalReminderDays = [14, 7, 3, 1, 0];
    public const int DefaultInactivityThresholdDays = 5;

    public static int[] ParseSessionReminderMinutes(string? raw) => Parse(raw, DefaultSessionReminderMinutes);

    public static int[] ParseCourseGoalReminderDays(string? raw) => Parse(raw, DefaultCourseGoalReminderDays);

    public static int GetInactivityThresholdDays(int raw) => raw > 0 ? raw : DefaultInactivityThresholdDays;

    private static int[] Parse(string? raw, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
        return values.Length > 0 ? values : fallback;
    }
}
