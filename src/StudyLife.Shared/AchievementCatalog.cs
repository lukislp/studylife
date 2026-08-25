namespace StudyLife.Shared;

/// <summary>
/// Single data-driven source of truth for the study-milestone achievements: 13 categories / 44
/// tiers total. Consumed by the client (Index.Achievements.razor.cs, which still owns the i18n
/// names/icons/UI), StudyMetrics.CountUnlockedAchievements (Wrapped.razor.cs, unlocked/total only),
/// and the server's push notifier (BackgroundTaskService.Reports.cs, RunAchievementCheckAsync -
/// hours/streak/sessions/courses/allcourses only, see its comment for why the other 8 categories
/// deliberately don't push). Replaces three previously hand-synced copies, one of which (the
/// server's) had already drifted: truncated tiers and a globally- instead of programme-scoped
/// course count (audit finding D1).
/// </summary>
public static class AchievementCatalog
{
    // Category keys match the server's SentReminder key format ("achievement:{key}:{threshold}")
    // exactly, for the 5 categories that push - don't rename without a migration plan, since
    // already-sent reminders in the DB reference these strings.
    public const string HoursKey = "hours";
    public const string StreakKey = "streak";
    public const string SessionsKey = "sessions";
    public const string CoursesKey = "courses";
    public const string AllCoursesKey = "allcourses";

    public static readonly int[] HoursTiers = { 25, 100, 500, 1000, 2000 };
    public static readonly int[] StreakTiers = { 7, 30, 100, 365 };
    public static readonly int[] SessionsTiers = { 50, 200, 500, 1000 };
    public static readonly int[] CoursesTiers = { 1, 10, 20, 30 };
    public static readonly int[] EarlyBirdTiers = { 5, 25, 100 };
    public static readonly int[] NightOwlTiers = { 5, 25, 100 };
    public static readonly int[] WeekendTiers = { 10, 50, 150 };
    public static readonly int[] MarathonTiers = { 2, 4, 6 };
    public static readonly int[] PerfectWeekTiers = { 1, 4, 12, 26, 52 };
    public static readonly int[] NotesTiers = { 5, 25, 100 };
    public static readonly int[] CourseDiversityTiers = { 2, 4, 6 };
    public static readonly int[] ProgramsTiers = { 1, 2, 3 };

    /// <summary>One threshold tier: whether `current` has reached `Threshold`, plus the raw
    /// current value (progress display, e.g. "18/25h").</summary>
    public readonly record struct Tier(int Threshold, bool Unlocked, double Current);

    /// <summary>Pure unlock computation for one category: one Tier per threshold, in threshold order.</summary>
    public static IReadOnlyList<Tier> BuildTiers(int[] thresholds, double current)
    {
        var tiers = new Tier[thresholds.Length];
        for (var i = 0; i < thresholds.Length; i++)
            tiers[i] = new Tier(thresholds[i], current >= thresholds[i], current);
        return tiers;
    }
}
