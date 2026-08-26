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

    // Stable keys for the 8 categories that are display-only (client/Wrapped/metrics API) and
    // never push a server notification (see BackgroundTaskService.Reports's RunAchievementCheckAsync
    // comment for why) - added for the metrics API's GET /api/metrics/achievements (MetricsController),
    // so every one of the 44 tiers has a single-sourced, stable category key, matching the 5
    // push-capable categories above.
    public const string EarlyBirdKey = "earlybird";
    public const string NightOwlKey = "nightowl";
    public const string WeekendKey = "weekend";
    public const string MarathonKey = "marathon";
    public const string PerfectWeekKey = "perfectweek";
    public const string NotesKey = "notes";
    public const string CourseDiversityKey = "coursediversity";
    public const string ProgramsKey = "programs";

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

    /// <summary>
    /// The 13 raw per-category counts that feed <see cref="BuildTiers"/> across every achievement
    /// category - see <see cref="BuildInputs"/>.
    /// </summary>
    public readonly record struct AchievementInputs(
        double TotalHours, int TotalSessions, int LongestStreak,
        int CoursesCompleted, bool AllCoursesDone,
        int EarlyBirdCount, int NightOwlCount, int WeekendCount, double LongestSessionHours,
        int PerfectWeeks, int NotesCount, int MaxCourseDiversity, int ProgramsCompleted);

    /// <summary>
    /// Gathers the raw achievement-tier inputs from studied session history - extracted from the
    /// two previously independent copies of this exact aggregation (client: Index.Achievements.
    /// razor.cs's BuildAchievements; server: BackgroundTaskService.Reports's
    /// RunAchievementCheckAsync, which only ever needed the first 5 fields for its 5 push-capable
    /// categories). Both call sites now share this one implementation; the metrics API
    /// (MetricsController, GET /api/metrics/achievements) uses it for all 13.
    /// <paramref name="studiedHistory"/> must already be "studied" (StudyMetrics.IsStudied).
    /// <paramref name="completedCourseIds"/>/<paramref name="activeCourseIds"/>: coursesCompleted
    /// is scoped to the active study programme's catalog (completedCourseIds ∩ activeCourseIds) -
    /// completedCourseIds itself spans every programme the user has ever had, see the two call
    /// sites' own comments on why an unscoped count would leak other programmes' completions in.
    /// <paramref name="notesCount"/>/<paramref name="programsCompleted"/> come from outside session
    /// history entirely (note count, IsCompleted-flagged study programs) - callers that don't need
    /// those two tiers (the server's push check) may pass 0.
    /// </summary>
    public static AchievementInputs BuildInputs(
        IReadOnlyList<StudySessionDto> studiedHistory,
        IReadOnlyCollection<int> completedCourseIds,
        IReadOnlyCollection<int> activeCourseIds,
        int weeklyGoalMinHours,
        int ectsTotal, int ectsEarned,
        int notesCount, int programsCompleted)
    {
        var totalHours = studiedHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = studiedHistory.Count;
        var longestStreak = StudyMetrics.CalcLongestStreak(studiedHistory.Select(s => s.StartTime));
        var coursesCompleted = completedCourseIds.Count(id => activeCourseIds.Contains(id));
        var allCoursesDone = ectsTotal > 0 && ectsEarned >= ectsTotal;

        var earlyBirdCount = studiedHistory.Count(s => s.StartTime.Hour < 7);
        var nightOwlCount = studiedHistory.Count(s => s.StartTime.Hour >= 22);
        var weekendCount = studiedHistory.Count(s => s.StartTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var longestSessionHours = studiedHistory.Count > 0 ? studiedHistory.Max(s => (s.EndTime - s.StartTime).TotalHours) : 0;

        var weeklyGroups = studiedHistory.GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime)).ToList();
        var perfectWeeks = weeklyGoalMinHours > 0
            ? weeklyGroups.Count(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours) >= weeklyGoalMinHours)
            : 0;
        var maxCourseDiversity = weeklyGroups.Count > 0
            ? weeklyGroups.Max(g => g.Select(s => s.CourseId).Distinct().Count())
            : 0;

        return new AchievementInputs(totalHours, totalSessions, longestStreak, coursesCompleted, allCoursesDone,
            earlyBirdCount, nightOwlCount, weekendCount, longestSessionHours,
            perfectWeeks, notesCount, maxCourseDiversity, programsCompleted);
    }
}
