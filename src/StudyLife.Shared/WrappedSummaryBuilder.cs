namespace StudyLife.Shared;

/// <summary>
/// Builds the wrapped year-in-review summary (see <see cref="WrappedSummaryDto"/>) from raw
/// inputs. Extracted verbatim from Wrapped.razor.cs's OnTextLoadedAsync and
/// BuildAchievementCountAsync, so client and server compute identical numbers - same LINQ, same
/// rounding, same ordering, same tie-breaking.
///
/// <see cref="Build"/> produces everything at once (what a server endpoint needs). The two phase
/// methods it composes are public because the page renders progressively: the recap slides
/// appear before the achievements slide, whose own all-time fetch is a separate, later phase.
/// </summary>
public static class WrappedSummaryBuilder
{
    /// <summary>Lookback of the recap period - a rolling window, not a "study year"/semester
    /// concept (the data model has no clean academic year).</summary>
    public const int PeriodHistoryDays = 365;

    /// <summary>Lookback behind the achievements count - the whole journey, same span as
    /// DashboardSummaryBuilder.AchievementHistoryDays.</summary>
    public const int AllTimeHistoryDays = 3650;

    /// <summary>Everything at once - what a server endpoint serves.</summary>
    public static WrappedSummaryDto Build(WrappedSummaryInput input) => new()
    {
        Recap = BuildRecap(input),
        Achievements = BuildAchievements(input),
    };

    /// <summary>
    /// Active-programme scope: AllCourses is already limited to the active programme, so this id
    /// set defines which sessions/notes belong to "this" programme.
    /// </summary>
    private static HashSet<int> ActiveCourseIds(WrappedSummaryInput input) =>
        input.AllCourses.Select(c => c.Id).ToHashSet();

    /// <summary>Phase 1: total hours/sessions, longest streak, top course, busiest weekday, and
    /// the chronotype (early bird/night owl) hours - all over the recap period.</summary>
    public static WrappedRecapDto BuildRecap(WrappedSummaryInput input)
    {
        var activeCourseIds = ActiveCourseIds(input);
        var periodHistory = input.PeriodHistory.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();

        var result = new WrappedRecapDto
        {
            TotalHours = periodHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours),
        };
        result.TotalHoursLabel = FormatHours(result.TotalHours);
        result.TotalSessions = periodHistory.Count;
        result.LongestStreak = StudyMetrics.CalcLongestStreak(periodHistory.Select(s => s.StartTime));

        var byCourse = periodHistory
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .FirstOrDefault();
        if (byCourse.Hours > 0)
        {
            var course = input.AllCourses.FirstOrDefault(c => c.Id == byCourse.CourseId);
            var sample = periodHistory.First(s => s.CourseId == byCourse.CourseId);
            result.TopCourse = new WrappedTopCourseDto
            {
                CourseId = byCourse.CourseId,
                Name = course?.Name ?? sample.CourseName,
                Icon = course?.Icon ?? "📚",
                Color = course?.Color ?? sample.CourseColor,
                Hours = byCourse.Hours,
            };
        }

        var hoursByWeekday = new double[7]; // 0=Monday .. 6=Sunday
        foreach (var s in periodHistory)
            hoursByWeekday[((int)s.StartTime.DayOfWeek + 6) % 7] += (s.EndTime - s.StartTime).TotalHours;
        var bestWeekdayIdx = 0;
        for (var i = 1; i < 7; i++)
            if (hoursByWeekday[i] > hoursByWeekday[bestWeekdayIdx]) bestWeekdayIdx = i;
        if (hoursByWeekday[bestWeekdayIdx] > 0)
        {
            result.BusiestWeekday = new WrappedBusiestWeekdayDto
            {
                Index = bestWeekdayIdx,
                Hours = hoursByWeekday[bestWeekdayIdx],
            };
        }

        result.EarlyBirdHours = periodHistory.Where(s => s.StartTime.Hour < 7).Sum(s => (s.EndTime - s.StartTime).TotalHours);
        result.NightOwlHours = periodHistory.Where(s => s.StartTime.Hour >= 22).Sum(s => (s.EndTime - s.StartTime).TotalHours);

        return result;
    }

    /// <summary>
    /// Phase 2: unlocked/total achievement tiers. Mirrors the inputs that
    /// Index.Achievements.razor.cs's BuildAchievements uses for the same 13 categories - see
    /// StudyMetrics.CountUnlockedAchievements for the actual thresholds. Deliberately all-time
    /// (AllTimeHistory), not limited to the recap period: achievements are programme-wide
    /// milestones, not a period comparison - a "before/after" split couldn't be cleanly derived
    /// for categories without a date reference (e.g. programmes completed; IsCompleted is a plain
    /// flag without a timestamp).
    /// </summary>
    public static WrappedAchievementsDto BuildAchievements(WrappedSummaryInput input)
    {
        var settings = input.Settings;
        var allCourses = input.AllCourses;
        var activeCourseIds = ActiveCourseIds(input);
        var allTimeHistory = input.AllTimeHistory.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();

        var totalHours = allTimeHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = allTimeHistory.Count;
        var longestStreak = StudyMetrics.CalcLongestStreak(allTimeHistory.Select(s => s.StartTime));
        var coursesCompleted = settings.CompletedCourseIds.Count(id => activeCourseIds.Contains(id));

        var ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, input.GroupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, input.GroupQuotas);
        var allCoursesDone = ectsTotal > 0 && ectsEarned >= ectsTotal;

        var earlyBirdCount = allTimeHistory.Count(s => s.StartTime.Hour < 7);
        var nightOwlCount = allTimeHistory.Count(s => s.StartTime.Hour >= 22);
        var weekendCount = allTimeHistory.Count(s => s.StartTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var longestSessionHours = allTimeHistory.Count > 0 ? allTimeHistory.Max(s => (s.EndTime - s.StartTime).TotalHours) : 0;

        var weeklyGroups = allTimeHistory.GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime)).ToList();
        var perfectWeeks = settings.WeeklyGoalMinHours > 0
            ? weeklyGroups.Count(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours) >= settings.WeeklyGoalMinHours)
            : 0;
        var maxCourseDiversity = weeklyGroups.Count > 0
            ? weeklyGroups.Max(g => g.Select(s => s.CourseId).Distinct().Count())
            : 0;

        var notesCount = input.Notes.Count(n => !n.CourseId.HasValue || activeCourseIds.Contains(n.CourseId.Value));
        var programsCompleted = input.StudyPrograms.Count(p => p.IsCompleted);

        var (unlocked, total) = StudyMetrics.CountUnlockedAchievements(
            totalHours, longestStreak, totalSessions, coursesCompleted, allCoursesDone,
            earlyBirdCount, nightOwlCount, weekendCount, longestSessionHours,
            perfectWeeks, notesCount, maxCourseDiversity, programsCompleted);

        return new WrappedAchievementsDto { Unlocked = unlocked, Total = total };
    }

    /// <summary>"3h 20m" - always shows the minute part, even when it is 0 (unlike
    /// DashboardSummaryBuilder's FormatHoursLabel, which omits a zero minute part). Kept
    /// byte-identical to the page's original private helper.</summary>
    private static string FormatHours(double hours) => $"{(int)hours}h {(int)((hours - (int)hours) * 60)}m";
}
