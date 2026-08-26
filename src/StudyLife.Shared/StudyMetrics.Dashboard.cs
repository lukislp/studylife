namespace StudyLife.Shared;

/// <summary>
/// Dashboard-aggregate metrics, extracted verbatim from their former sole call sites
/// (Stats.razor.cs/Report.razor.cs, Index.Insights.razor.cs, Index.razor.cs,
/// Index.Forecast.razor.cs) as part of the metrics API (see MetricsController) - owner
/// decision: every metric computed in exactly ONE place, served by the server, with the
/// client calling the same in-process function it always did (offline capability, zero
/// duplication since it's the same code). See docs/ARCHITECTURE.md "Metrics API".
/// </summary>
public static partial class StudyMetrics
{
    /// <summary>
    /// Per-course aggregate hours/session-count, extracted from Stats.razor.cs's course-list
    /// build (and Report.razor.cs's identical copy). Record struct instead of a value tuple -
    /// LINQ over a List&lt;(...)&gt; of value tuples has triggered a Mono AOT crash at compile
    /// time (not call time) in the native app shell (studylife-app, BlazorWebView) - see
    /// project_studylife_app_ios_aot_linq_tuple_crash.
    /// </summary>
    public readonly record struct CourseHoursResult(CourseDto Course, double Hours, int SessionCount);

    /// <summary>
    /// Studied hours/session-count per course, for every course that is either selected,
    /// completed, or has at least one session - and actually has ≥1 studied session (courses
    /// with zero studied sessions are omitted entirely, not returned as a zero row). Deliberately
    /// UNSORTED (call sites sort differently: Stats.razor.cs by hours descending after computing
    /// the max for its progress bars, Report.razor.cs by semester, the metrics API by hours
    /// descending per the wire contract) - sorting is a presentation concern, not part of this
    /// calculation.
    /// </summary>
    public static List<CourseHoursResult> CalcCourseHours(
        IReadOnlyList<CourseDto> allCourses,
        IEnumerable<int> selectedCourseIds,
        IEnumerable<int> completedCourseIds,
        IEnumerable<StudySessionDto> sessions,
        DateTime now)
    {
        var sessionList = sessions as IReadOnlyList<StudySessionDto> ?? sessions.ToList();
        var relevantIds = selectedCourseIds
            .Concat(completedCourseIds)
            .Concat(sessionList.Select(s => s.CourseId))
            .Distinct();

        var result = new List<CourseHoursResult>();
        foreach (var id in relevantIds)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == id);
            if (course == null) continue;
            var completedSessions = sessionList.Where(s => s.CourseId == id && IsStudied(s, now)).ToList();
            if (completedSessions.Count == 0) continue;
            var hours = completedSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
            result.Add(new CourseHoursResult(course, hours, completedSessions.Count));
        }
        return result;
    }

    /// <summary>
    /// Lookback window for <see cref="CalcNeglectedCourse"/>: only sessions within this many
    /// days count toward "last studied", so a course studied once years ago doesn't rank above
    /// one genuinely never touched. Extracted from Index.razor.cs's former NeglectHistoryDays.
    /// </summary>
    public const int NeglectedCourseHistoryDays = 180;

    /// <summary>Result of <see cref="CalcNeglectedCourse"/> - the least-recently-studied active course.</summary>
    public readonly record struct NeglectedCourseResult(CourseDto Course, DateTime? LastStudied);

    /// <summary>
    /// The active (selected, not-yet-completed) course studied longest ago, or never - extracted
    /// from Index.Insights.razor.cs's BuildNeglectedCourse. Requires at least 2 active courses
    /// (with just one, "neglected" is meaningless - it's simply the only thing being studied);
    /// returns null below that gate. Ties (including "never studied") resolve to the first
    /// course in <paramref name="allCourses"/> order - a manual loop instead of
    /// `.OrderBy(...).First()` over a value-tuple projection, for the same AOT reason as
    /// <see cref="CourseHoursResult"/>.
    /// </summary>
    public static NeglectedCourseResult? CalcNeglectedCourse(
        IReadOnlyList<CourseDto> allCourses,
        IReadOnlyCollection<int> selectedCourseIds,
        IReadOnlyCollection<int> completedCourseIds,
        IEnumerable<StudySessionDto> studiedHistory,
        DateTime today)
    {
        var activeCourses = allCourses
            .Where(c => selectedCourseIds.Contains(c.Id) && !completedCourseIds.Contains(c.Id))
            .ToList();
        if (activeCourses.Count < 2) return null;

        var cutoff = today.AddDays(-NeglectedCourseHistoryDays);
        var lastStudiedByCourse = studiedHistory
            .Where(s => s.StartTime.Date >= cutoff)
            .GroupBy(s => s.CourseId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.StartTime));

        NeglectedCourseResult? best = null;
        foreach (var course in activeCourses)
        {
            var lastStudied = lastStudiedByCourse.TryGetValue(course.Id, out var d) ? (DateTime?)d : null;
            if (best == null || (lastStudied ?? DateTime.MinValue) < (best.Value.LastStudied ?? DateTime.MinValue))
                best = new NeglectedCourseResult(course, lastStudied);
        }
        return best;
    }

    /// <summary>Result of <see cref="CalcTopicsProgress"/>.</summary>
    public readonly record struct TopicsProgressResult(int Completed, int Total, double Percent);

    /// <summary>
    /// Sums CourseDto.Topics vs. CourseGoalDto.CompletedTopics across the given courses (the
    /// dashboard's Setup.razor topic checklist, aggregated) - extracted verbatim from
    /// Index.razor.cs's inline loop. Courses without any topics defined don't contribute to the
    /// total (avoids a misleadingly small denominator).
    /// </summary>
    public static TopicsProgressResult CalcTopicsProgress(
        IEnumerable<CourseDto> allCourses,
        IReadOnlyCollection<int> selectedCourseIds,
        IReadOnlyList<CourseGoalDto> goals)
    {
        var completed = 0;
        var total = 0;
        foreach (var course in allCourses.Where(c => selectedCourseIds.Contains(c.Id)))
        {
            if (course.Topics.Count == 0) continue;
            total += course.Topics.Count;
            var goal = goals.FirstOrDefault(g => g.CourseId == course.Id);
            var completedTopics = string.IsNullOrWhiteSpace(goal?.CompletedTopics)
                ? new HashSet<string>()
                : goal.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            completed += course.Topics.Count(t => completedTopics.Contains(t));
        }
        var percent = total > 0 ? Math.Min(100.0, completed / (double)total * 100) : 0;
        return new TopicsProgressResult(completed, total, percent);
    }

    /// <summary>Result of <see cref="CalcMonthComparison"/>. DeltaVsPreviousMonth/DeltaVsLastYear
    /// are signed (positive = more than the comparison period).</summary>
    public readonly record struct MonthComparisonResult(
        double CurrentMonthHours,
        double PreviousMonthHours,
        double DeltaVsPreviousMonth,
        bool HasYearData,
        double? SameMonthLastYearHours,
        double? DeltaVsLastYear);

    /// <summary>
    /// Month-over-month and year-over-year studied-hours comparison, extracted from
    /// Index.Forecast.razor.cs's BuildMonthComparison. <paramref name="allTimeHistory"/> is
    /// expected to already be "studied" (the app's own long-range fetch defaults to
    /// onlyCompleted=true, see /api/sessions/history). The year-over-year fields are only
    /// populated once history actually reaches back over the whole same calendar month last
    /// year - otherwise a "0h" comparison would be misleading, not informative.
    /// </summary>
    public static MonthComparisonResult CalcMonthComparison(IEnumerable<StudySessionDto> allTimeHistory, DateTime today)
    {
        var history = allTimeHistory as IReadOnlyList<StudySessionDto> ?? allTimeHistory.ToList();
        double HoursInMonth(int year, int month) => history
            .Where(s => s.StartTime.Year == year && s.StartTime.Month == month)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var thisMonthHours = HoursInMonth(today.Year, today.Month);
        var lastMonthDate = today.AddMonths(-1);
        var lastMonthHours = HoursInMonth(lastMonthDate.Year, lastMonthDate.Month);
        var lastYearDate = today.AddYears(-1);
        var lastYearHours = HoursInMonth(lastYearDate.Year, lastYearDate.Month);

        var earliestSession = history.Count > 0 ? history.Min(s => s.StartTime) : (DateTime?)null;
        var lastYearMonthStart = new DateTime(lastYearDate.Year, lastYearDate.Month, 1);
        var hasYearData = earliestSession.HasValue && earliestSession.Value.Date <= lastYearMonthStart;

        return new MonthComparisonResult(
            thisMonthHours,
            lastMonthHours,
            thisMonthHours - lastMonthHours,
            hasYearData,
            hasYearData ? lastYearHours : null,
            hasYearData ? thisMonthHours - lastYearHours : null);
    }

    /// <summary>One open course goal with a target date, extracted from Index.razor.cs's inline
    /// upcoming-goals projection.</summary>
    public readonly record struct UpcomingCourseGoal(int CourseId, string CourseName, DateTime TargetDate, int DaysLeft);

    /// <summary>
    /// Open (not yet completed) course goals with a target date, soonest first, capped at
    /// <paramref name="max"/>. DaysLeft is deliberately UNCAPPED at the low end (negative =
    /// overdue) - same convention as the dashboard's course-pill countdown badge.
    /// </summary>
    public static List<UpcomingCourseGoal> CalcUpcomingCourseGoals(IEnumerable<CourseGoalDto> goals, DateTime today, int max = 5)
    {
        return goals
            .Where(g => g.TargetDate.HasValue && g.CompletedAt == null)
            .OrderBy(g => g.TargetDate)
            .Take(max)
            .Select(g => new UpcomingCourseGoal(g.CourseId, g.CourseName, g.TargetDate!.Value, (g.TargetDate.Value.Date - today).Days))
            .ToList();
    }
}
