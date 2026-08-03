namespace StudyLife.Shared;

/// <summary>
/// Shared, stateless metric calculations (streak, ECTS forecast, quota, average grade)
/// for the dashboard (Index.razor), analytics (Stats.razor), and server (BackgroundTaskService).
/// Replaces the previously three hand-maintained copies of the same logic - changes here take
/// effect everywhere at once. The Home Assistant integration (coordinator.py) remains a deliberately
/// parallel Python implementation with identical semantics.
/// </summary>
public static class StudyMetrics
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
    /// ECTS-weighted average grade (Grade × Ects / sum of Ects), falls back to the
    /// unweighted mean when the Ects sum is 0. Null for empty input.
    /// </summary>
    public static decimal? CalcWeightedAverageGrade(IEnumerable<(decimal Grade, int Ects)> gradedCourses)
    {
        var grades = gradedCourses.ToList();
        if (grades.Count == 0) return null;
        var totalEcts = grades.Sum(g => g.Ects);
        return totalEcts > 0
            ? grades.Sum(g => g.Grade * g.Ects) / totalEcts
            : grades.Average(g => g.Grade);
    }

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
    /// entire bar; warning as long as the minimum goal is not yet met.
    /// </summary>
    public static QuotaResult CalcQuota(double hours, double targetMinHours, double targetMaxHours)
    {
        var maxBar = targetMaxHours * 1.15;
        var percent = Math.Min(100, hours / maxBar * 100);
        var minPercent = Math.Min(100, targetMinHours / maxBar * 100);
        var warning = hours < targetMinHours;
        return new QuotaResult(percent, minPercent, warning, warning ? targetMinHours - hours : 0);
    }

    /// <summary>
    /// Prorated monthly goal: absolute monthly goal prorated by elapsed weeks, so that
    /// the start of the month doesn't misleadingly look "behind". Both week counts via
    /// ceil(days/7), weeksElapsed capped at totalWeeksInMonth - mirrors _calc_month_quota
    /// in the Home Assistant integration (coordinator.py) exactly.
    /// </summary>
    public static (int TargetMinHours, int TargetMaxHours) ProrateMonthlyTarget(
        int monthlyGoalMinHours, int monthlyGoalMaxHours, DateTime today)
    {
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var totalWeeksInMonth = Math.Max(1, Math.Ceiling(daysInMonth / 7.0));
        var weeksElapsed = Math.Min(totalWeeksInMonth, Math.Max(1, Math.Ceiling((today - monthStart).TotalDays / 7.0)));
        return (
            (int)Math.Round(monthlyGoalMinHours * weeksElapsed / totalWeeksInMonth),
            (int)Math.Round(monthlyGoalMaxHours * weeksElapsed / totalWeeksInMonth));
    }

    /// <summary>
    /// Pure counting variant of the achievement tiers from Index.Achievements.razor.cs
    /// (BuildAchievements): the same 13 categories/44 tiers, but only as (Unlocked, Total) -
    /// for the study year-in-review (Wrapped.razor.cs), which doesn't need individual achievement
    /// names/icons, just the total number of unlocked tiers. Deliberate, documented duplication:
    /// when BuildAchievements' thresholds change, this list must be kept manually in sync,
    /// since Index.Achievements.razor.cs additionally needs i18n names per tier
    /// and therefore can't be moved here 1:1.
    /// </summary>
    public static (int Unlocked, int Total) CountUnlockedAchievements(
        double totalHours, int longestStreak, int totalSessions, int coursesCompleted, bool allCoursesDone,
        int earlyBirdCount, int nightOwlCount, int weekendCount, double longestSessionHours,
        int perfectWeeks, int notesCount, int maxCourseDiversity, int programsCompleted)
    {
        var tiers = new List<bool>();
        foreach (var t in new[] { 25, 100, 500, 1000, 2000 }) tiers.Add(totalHours >= t);
        foreach (var t in new[] { 7, 30, 100, 365 }) tiers.Add(longestStreak >= t);
        foreach (var t in new[] { 50, 200, 500, 1000 }) tiers.Add(totalSessions >= t);
        foreach (var t in new[] { 1, 10, 20, 30 }) tiers.Add(coursesCompleted >= t);
        tiers.Add(allCoursesDone);
        foreach (var t in new[] { 5, 25, 100 }) tiers.Add(earlyBirdCount >= t);
        foreach (var t in new[] { 5, 25, 100 }) tiers.Add(nightOwlCount >= t);
        foreach (var t in new[] { 10, 50, 150 }) tiers.Add(weekendCount >= t);
        foreach (var t in new[] { 2, 4, 6 }) tiers.Add(longestSessionHours >= t);
        foreach (var t in new[] { 1, 4, 12, 26, 52 }) tiers.Add(perfectWeeks >= t);
        foreach (var t in new[] { 5, 25, 100 }) tiers.Add(notesCount >= t);
        foreach (var t in new[] { 2, 4, 6 }) tiers.Add(maxCourseDiversity >= t);
        foreach (var t in new[] { 1, 2, 3 }) tiers.Add(programsCompleted >= t);
        return (tiers.Count(x => x), tiers.Count);
    }
}
