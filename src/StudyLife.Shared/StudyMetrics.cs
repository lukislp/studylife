namespace StudyLife.Shared;

/// <summary>
/// Shared, stateless metric calculations (streak, ECTS forecast, quota, average grade)
/// for the dashboard (Index.razor), analytics (Stats.razor), and server (BackgroundTaskService).
/// Replaces the previously three hand-maintained copies of the same logic - changes here take
/// effect everywhere at once. The Home Assistant integration (coordinator.py) remains a deliberately
/// parallel Python implementation with identical semantics.
///
/// Split across partial files by concern, same convention as BackgroundTaskService.*.cs: this
/// file keeps the original core (streak/quota/forecast/grade), StudyMetrics.Dashboard.cs holds
/// the dashboard-aggregate functions extracted from Index.razor.cs/Stats.razor.cs for the
/// metrics API (see MetricsController), and StudyMetrics.WeeklyReport.cs the HA-facing
/// last-completed-week variant.
/// </summary>
public static partial class StudyMetrics
{
    /// <summary>
    /// "Studied" = timer completed OR the planned end lies in the past
    /// (not every session runs through the in-app timer, e.g. offline reading by the lake).
    /// </summary>
    public static bool IsStudied(StudySessionDto s, DateTime now) => s.IsCompleted || s.EndTime <= now;

    /// <summary>Monday start of the week of the given date (modulo offset, Monday = 0).</summary>
    public static DateTime WeekStartOf(DateTime date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-offset);
    }

    /// <summary>
    /// CURRENT streak: anchor is today, or yesterday if there's been no studying today yet -
    /// the streak lives until midnight instead of jumping to 0 every morning.
    /// </summary>
    public static int CalcStreak(IEnumerable<DateTime> studyTimes, DateTime today)
    {
        var dates = studyTimes.Select(d => d.Date).ToHashSet();
        var day = dates.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (dates.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>Longest streak ever reached (distinct calendar days, unbroken sequence).</summary>
    public static int CalcLongestStreak(IEnumerable<DateTime> studyTimes)
    {
        var dates = studyTimes.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
        if (dates.Count == 0) return 0;
        var longest = 1;
        var current = 1;
        for (var i = 1; i < dates.Count; i++)
        {
            current = (dates[i] - dates[i - 1]).Days == 1 ? current + 1 : 1;
            longest = Math.Max(longest, current);
        }
        return longest;
    }

    /// <summary>
    /// A single course's grade + ECTS weight, as consumed by <see cref="CalcWeightedAverageGrade(IEnumerable{GradedCourse})"/>.
    /// A record struct rather than a value tuple: LINQ generic-instantiated over a value-tuple
    /// element type has reproducibly crashed the MAUI app's Mono AOT compiler on iOS (SIGABRT at
    /// startup, from the method merely being AOT-compiled, not from ever being called - see
    /// INativeHealthData/Stats.Health.razor.cs for the same class of bug and the full writeup).
    /// A record struct doesn't hit that gsharedvt code-generation bug, so callers of this overload
    /// are free to use normal LINQ (Select/Where/etc.) again.
    /// </summary>
    public readonly record struct GradedCourse(decimal Grade, int Ects);

    /// <summary>
    /// ECTS-weighted average grade (Grade × Ects / sum of Ects), falls back to the
    /// unweighted mean when the Ects sum is 0. Null for empty input.
    /// </summary>
    public static decimal? CalcWeightedAverageGrade(IEnumerable<GradedCourse> gradedCourses)
    {
        var grades = gradedCourses.ToList();
        if (grades.Count == 0) return null;
        var totalEcts = grades.Sum(g => g.Ects);
        return totalEcts > 0
            ? grades.Sum(g => g.Grade * g.Ects) / totalEcts
            : grades.Average(g => g.Grade);
    }

    // A bare NumberFormatInfo, NOT `new CultureInfo("de-DE")`: the Blazor WASM client publishes
    // with InvariantGlobalization=true, where constructing any non-invariant culture throws
    // CultureNotFoundException - and because this is a static field, that exception became a
    // TypeInitializationException killing EVERY StudyMetrics call on the dashboard/stats pages
    // (hit live in 1.43.2). All we need is the comma decimal separator; NumberFormatInfo carries
    // no culture lookup and can never throw here.
    private static readonly System.Globalization.NumberFormatInfo GradeDisplayFormat =
        new() { NumberDecimalSeparator = "," };

    /// <summary>
    /// Formats a grade with a comma decimal separator (e.g. "1,70") - the documented
    /// convention for grade display regardless of which language/culture the rest of the UI is
    /// shown in (German-style grading scale, see CalcWeightedAverageGrade). Explicit via a fixed
    /// "de-DE" CultureInfo rather than ".ToString(...).Replace('.', ',')" - same output, but the
    /// intention (a specific display convention, not an accident of the current locale) is
    /// visible in the code, and it lives in exactly one place instead of being hand-rolled at
    /// every call site (Index.razor.cs/Stats.razor.cs both used to duplicate the Replace() call).
    /// </summary>
    public static string FormatGrade(decimal grade) => grade.ToString("0.00", GradeDisplayFormat);

    /// <summary>
    /// Same comma-decimal convention as <see cref="FormatGrade(decimal)"/>, for the handful of
    /// call sites that need a different precision (e.g. "0.0" for a compact chart label) than
    /// the default "0.00" - still just one place owning the separator choice.
    /// </summary>
    public static string FormatGrade(decimal grade, string format) => grade.ToString(format, GradeDisplayFormat);

    /// <summary>
    /// Result of <see cref="CalcForecast"/>. BaselineWeeksNeeded / RecentWeeklyHours /
    /// ReferenceWeeklyHours are only populated when Available=true - Index.razor additionally
    /// needs them for the graduation goal card (reverse calculation), Stats.razor only the date.
    /// </summary>
    public readonly record struct ForecastResult(
        bool Available,
        bool AlreadyDone,
        DateTime? ForecastDate,
        double BaselineWeeksNeeded,
        double RecentWeeklyHours,
        double ReferenceWeeklyHours);

    /// <summary>
    /// ECTS graduation forecast. Semester-based baseline (one semester = 6 months = 26 weeks,
    /// ECTS distributed evenly across all semesters of the catalog) instead of an extrapolation from
    /// CourseGoalDto.CompletedAt: anyone who marks already-completed courses as "completed" in the
    /// app only after the fact would otherwise get a tiny time span between the earliest
    /// CompletedAt and today - that made the forecast look absurdly optimistic. Refined with
    /// the actual study rate of the last 8 weeks relative to the configured weekly workload,
    /// pace ratio clamped to [0.25, 3.0].
    /// </summary>
    public static ForecastResult CalcForecast(
        int ectsTotal,
        int ectsEarned,
        IReadOnlyList<CourseDto> allCourses,
        int weeklyGoalMinHours,
        int weeklyGoalMaxHours,
        IEnumerable<StudySessionDto> history,
        DateTime now)
    {
        var remainingEcts = ectsTotal - ectsEarned;
        if (remainingEcts <= 0)
            return new ForecastResult(false, ectsTotal > 0, null, 0, 0, 0);

        var totalSemesters = allCourses.Count > 0 ? allCourses.Max(c => c.Semester) : 0;
        if (totalSemesters <= 0)
            return new ForecastResult(false, false, null, 0, 0, 0);

        var ectsPerSemester = (double)ectsTotal / totalSemesters;
        var baselineWeeksNeeded = remainingEcts / ectsPerSemester * 26.0;

        const int recentWeeks = 8;
        var today = now.Date;
        var recentCutoff = today.AddDays(-recentWeeks * 7);
        var recentHours = history
            .Where(s => s.StartTime.Date >= recentCutoff && IsStudied(s, now))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var recentWeeklyHours = recentHours / recentWeeks;

        var referenceWeeklyHours = (weeklyGoalMinHours + weeklyGoalMaxHours) / 2.0;
        var paceRatio = recentWeeklyHours > 0 ? recentWeeklyHours / referenceWeeklyHours : 1.0;
        paceRatio = Math.Clamp(paceRatio, 0.25, 3.0);

        var adjustedWeeksNeeded = baselineWeeksNeeded / paceRatio;
        var forecastDate = today.AddDays(adjustedWeeksNeeded * 7);
        return new ForecastResult(true, false, forecastDate, baselineWeeksNeeded, recentWeeklyHours, referenceWeeklyHours);
    }

    /// <summary>Result of <see cref="CalcQuota"/>. MissingHours is 0 when there is no shortfall.</summary>
    public readonly record struct QuotaResult(double Percent, double MinPercent, bool Warning, double MissingHours);

    /// <summary>
    /// Quota progress against a min/max hours goal (weekly as well as monthly). The bar
    /// scales to 115% of the maximum goal, so that "goal reached" doesn't already fill the
    /// entire bar; warning as long as the minimum goal is not yet met. A zero max goal (no
    /// goal configured) has no bar to fill against, so it reads as 0%/no-warning rather than
    /// the NaN/Infinity a 0/0 division would otherwise produce - matches the Python Home
    /// Assistant port (coordinator.py), which returns 0% for this case, and keeps the result
    /// JSON-serializable (NaN isn't valid JSON).
    /// </summary>
    public static QuotaResult CalcQuota(double hours, double targetMinHours, double targetMaxHours)
    {
        var maxBar = targetMaxHours * 1.15;
        if (maxBar <= 0)
            return new QuotaResult(0, 0, false, 0);

        var percent = Math.Min(100, hours / maxBar * 100);
        var minPercent = Math.Min(100, targetMinHours / maxBar * 100);
        var warning = hours < targetMinHours;
        return new QuotaResult(percent, minPercent, warning, warning ? targetMinHours - hours : 0);
    }

    /// <summary>
    /// Pure counting variant of the achievement tiers from Index.Achievements.razor.cs
    /// (BuildAchievements): the same 13 categories/44 tiers, but only as (Unlocked, Total) -
    /// for the study year-in-review (Wrapped.razor.cs), which doesn't need individual achievement
    /// names/icons, just the total number of unlocked tiers. Thresholds/unlock computation come
    /// from AchievementCatalog, so this stays in sync with BuildAchievements automatically -
    /// only the counting-vs-display shape differs, which is why this isn't a 1:1 reuse of
    /// BuildAchievements (that one additionally needs i18n names per tier).
    /// </summary>
    public static (int Unlocked, int Total) CountUnlockedAchievements(
        double totalHours, int longestStreak, int totalSessions, int coursesCompleted, bool allCoursesDone,
        int earlyBirdCount, int nightOwlCount, int weekendCount, double longestSessionHours,
        int perfectWeeks, int notesCount, int maxCourseDiversity, int programsCompleted)
    {
        var tiers = new List<bool>();
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.HoursTiers, totalHours).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.StreakTiers, longestStreak).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.SessionsTiers, totalSessions).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.CoursesTiers, coursesCompleted).Select(t => t.Unlocked));
        tiers.Add(allCoursesDone);
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.EarlyBirdTiers, earlyBirdCount).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.NightOwlTiers, nightOwlCount).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.WeekendTiers, weekendCount).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.MarathonTiers, longestSessionHours).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.PerfectWeekTiers, perfectWeeks).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.NotesTiers, notesCount).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.CourseDiversityTiers, maxCourseDiversity).Select(t => t.Unlocked));
        tiers.AddRange(AchievementCatalog.BuildTiers(AchievementCatalog.ProgramsTiers, programsCompleted).Select(t => t.Unlocked));
        return (tiers.Count(x => x), tiers.Count);
    }
}
