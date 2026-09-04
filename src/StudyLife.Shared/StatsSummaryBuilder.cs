using System.Globalization;

namespace StudyLife.Shared;

/// <summary>
/// The raw inputs the statistics page has after its fetches - exactly what Stats.razor.cs's
/// OnInitializingAsync/OnTextLoadedAsync load, mapped to shared types so the server can assemble
/// the same input from the database. <see cref="Now"/> is the caller's wall clock: nothing in
/// <see cref="StatsSummaryBuilder"/> ever reads DateTime.Now/Today itself, so the result is a
/// pure function of this object (and therefore cacheable and testable).
///
/// Every list below is UNSCOPED (as fetched); the builder applies the active-programme filter
/// itself, exactly where the page applied it - so the cross-programme comparison still sees
/// everything while every other card stays scoped.
/// </summary>
public class StatsSummaryInput
{
    public UserSettingsDto Settings { get; set; } = new();

    /// <summary>All courses of the ACTIVE study programme (GET /api/courses is already
    /// programme-scoped). Defines the id set every session/goal/note below is filtered through.</summary>
    public List<CourseDto> AllCourses { get; set; } = new();

    /// <summary>The near-term session list (GET /api/sessions). Kept on the input (both callers
    /// still fill it) but no longer read by the builder: the course rows it used to feed now come
    /// from <see cref="HeavyHistory"/>, so a course last studied before this window keeps its row.</summary>
    public List<StudySessionDto> Sessions { get; set; } = new();

    /// <summary>Long-range history (GET /api/sessions/history?days=<see cref="HistoryDays"/>),
    /// behind the heatmap, donut, rhythm charts, monthly/weekly trends and the forecast.</summary>
    public List<StudySessionDto> History { get; set; } = new();

    /// <summary>All-time history (GET /api/sessions/history?days=<see cref="AllTimeHistoryDays"/>)
    /// - the course list rows/totals (see StudyMetrics.CalcCourseHours) and the semester
    /// comparison, whose earlier semesters mostly fall outside the <see cref="HistoryDays"/>
    /// window. Needed by phase 1, not just phase 3.</summary>
    public List<StudySessionDto> HeavyHistory { get; set; } = new();

    /// <summary>GET /api/coursegoals, unscoped: the cross-programme comparison needs the full
    /// list, every other card gets the active-programme subset.</summary>
    public List<CourseGoalDto> Goals { get; set; } = new();

    /// <summary>ECTS quotas per elective group of the active programme.</summary>
    public IReadOnlyDictionary<string, int> GroupQuotas { get; set; } = new Dictionary<string, int>();

    /// <summary>GET /api/studyprograms - drives the cross-programme comparison's rows and its
    /// "fewer than two programmes = hide the card" gate.</summary>
    public List<StudyProgramSummaryDto> StudyPrograms { get; set; } = new();

    /// <summary>One entry per programme of <see cref="StudyPrograms"/>: its course catalog and
    /// its group quotas. Replaces the client's N+1 fan-out (GET api/courses?program={id} plus GET
    /// api/studyprograms/{id} per programme) with a single input the server can load in one go. A
    /// programme without a matching entry is treated exactly like a failed fetch (empty catalog,
    /// empty quotas).</summary>
    public List<StatsProgramCatalogDto> ProgramCatalogs { get; set; } = new();

    /// <summary>GET /api/notes, unscoped - the builder keeps general (course-less) notes and
    /// those of the active programme, as the page does.</summary>
    public List<NoteDto> Notes { get; set; } = new();

    /// <summary>The caller's wall clock.</summary>
    public DateTime Now { get; set; }

    /// <summary>Derived from <see cref="Now"/> - never a separate clock read.</summary>
    public DateTime Today => Now.Date;
}

/// <summary>
/// Builds the whole statistics summary (see <see cref="StatsSummaryDto"/>) from raw inputs.
/// Extracted verbatim from Stats.razor.cs and its Stats.Charts/Trends/Comparisons/Grades/Programs
/// partials so that client and server compute identical numbers - same LINQ, same rounding, same
/// ordering, same tie-breaking, same magic constants.
///
/// <see cref="Build"/> produces everything at once (what a server endpoint needs). The three
/// phase methods it composes are public because the page renders progressively: it copies each
/// group into its fields at its own render point, so the cards keep appearing exactly when they
/// do today instead of all waiting for the slowest (~10-year) fetch.
/// </summary>
public static class StatsSummaryBuilder
{
    /// <summary>Lookback of the shared long-range history fetch - 53 full weeks plus the current
    /// one, so the calendar heatmap's grid is completely covered.</summary>
    public const int HistoryDays = 371;

    /// <summary>Lookback of the separate all-time fetch behind the semester comparison - the same
    /// 10-year convention as the dashboard's DashboardSummaryBuilder.AchievementHistoryDays.</summary>
    public const int AllTimeHistoryDays = 3650;

    /// <summary>Minimum notes within the 12-week window before the correlation card shows a chart
    /// instead of its empty state.</summary>
    public const int NotesCorrelationMinNotes = 5;

    /// <summary>Everything at once - what a server endpoint serves.</summary>
    public static StatsSummaryDto Build(StatsSummaryInput input) => new()
    {
        Core = BuildCore(input),
        Notes = BuildNotes(input),
        Extended = BuildExtended(input),
    };

    /// <summary>
    /// Active-programme scope: AllCourses is already limited to the active programme, so this id
    /// set defines which sessions/goals/notes belong to "this" programme. Custom course ids never
    /// collide with the built-in catalog (1-62) thanks to CustomCourseIdOffset (100000+), so the
    /// filter is unambiguous.
    /// </summary>
    private static HashSet<int> ActiveCourseIds(StatsSummaryInput input) =>
        input.AllCourses.Select(c => c.Id).ToHashSet();

    // ── Phase 1: settings + courses + goals + sessions + the 12-month history ──

    /// <summary>
    /// Phase 1: the course list, the summary/progress tiles and the large majority of the charts.
    /// Everything here is scoped to the active programme, so switching programmes really only
    /// shows its data.
    /// </summary>
    public static StatsCoreSummaryDto BuildCore(StatsSummaryInput input)
    {
        var settings = input.Settings;
        var allCourses = input.AllCourses;
        var now = input.Now;
        var today = input.Today;

        var activeCourseIds = ActiveCourseIds(input);
        var goals = input.Goals.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();
        var history = input.History.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        // Hours mean STUDIED hours on this page - a session that is merely scheduled for tonight
        // must not already appear in a heatmap cell, a donut slice or a monthly stack. The
        // charts below therefore consume `studiedHistory`; `history` stays for the helpers that
        // apply the same filter themselves. See docs/ARCHITECTURE.md "Number semantics".
        var studiedHistory = history.Where(s => StudyMetrics.IsStudied(s, now)).ToList();
        var allTimeHistory = input.HeavyHistory.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();

        var result = new StatsCoreSummaryDto();

        // Selected + completed + courses that actually have sessions - StudyMetrics.CalcCourseHours
        // computes this relevant-id set internally from the three raw inputs below. Deliberately
        // the FULL history, not the near-term session window: a course finished two semesters ago
        // otherwise dropped out of the list entirely, taking its hours, its trend arrow and its
        // sparkline with it even though all three were computable.
        var raw = StudyMetrics.CalcCourseHours(
            allCourses, settings.SelectedCourseIds, settings.CompletedCourseIds, allTimeHistory, now);

        var maxHours = raw.Count == 0 ? 1 : Math.Max(1, raw.Max(r => r.Hours));
        var trends = BuildCourseTrends(history, now, today);
        var sparks = BuildCourseSparks(history, now, today);

        result.CourseRows = raw
            .OrderByDescending(r => r.Hours)
            .Select(r =>
            {
                var goal = goals.FirstOrDefault(g => g.CourseId == r.Course.Id);
                var isCompleted = settings.CompletedCourseIds.Contains(r.Course.Id);
                // A completed course has no remaining deadline - without this guard a course
                // finished after its target date showed "goal overdue by N days" right next to its
                // "completed" badge.
                int? daysRemaining = !isCompleted && goal?.TargetDate.HasValue == true
                    ? (goal!.TargetDate!.Value.Date - today).Days
                    : null;
                return new StatsCourseRowDto
                {
                    Course = r.Course,
                    Hours = r.Hours,
                    SessionCount = r.SessionCount,
                    IsCompleted = isCompleted,
                    DaysRemaining = daysRemaining,
                    CompletionNote = goal?.CompletionNote,
                    Grade = goal?.Grade,
                    BarPercent = Math.Min(100, r.Hours / maxHours * 100),
                    TrendPercent = trends.GetValueOrDefault(r.Course.Id),
                    Spark = sparks.GetValueOrDefault(r.Course.Id),
                    RingPercent = CalcEctsRingPercent(r.Course, isCompleted, goal),
                    EctsEarned = isCompleted ? r.Course.Ects : 0,
                };
            })
            .ToList();

        result.TotalSessions = result.CourseRows.Sum(r => r.SessionCount);
        var totalHours = result.CourseRows.Sum(r => r.Hours);
        result.TotalHoursLabel = FormatHoursLabel(totalHours);

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        if (averageGrade.HasValue)
            result.AverageGradeLabel = StudyMetrics.FormatGrade(averageGrade.Value);

        result.GradeHistory = BuildGradeHistory(goals, allCourses);
        result.GradeTimeline = BuildGradeTimeline(goals, allCourses);
        result.HoursGradeScatter = BuildHoursGradeScatter(goals, allCourses, raw);
        result.HoursEctsScatter = BuildHoursEctsScatter(allCourses, raw, settings);
        result.GradeDistribution = BuildGradeDistribution(goals, allCourses);

        // Programme-aware: quotas of the ACTIVE programme (built-in: static, otherwise via fetch).
        result.EctsTotal = CourseCatalog.CalcTotalEcts(allCourses, input.GroupQuotas);
        result.EctsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, input.GroupQuotas);
        result.EctsPercent = result.EctsTotal > 0 ? Math.Min(100.0, result.EctsEarned / (double)result.EctsTotal * 100) : 0;

        result.Forecast = BuildForecast(settings, allCourses, history, result.EctsTotal, result.EctsEarned, now);
        result.Heatmap = BuildHeatmap(studiedHistory, allCourses, today);
        result.Donut = BuildDonut(studiedHistory, allCourses, today);
        result.Rhythm = BuildRhythm(studiedHistory);
        result.TimeHeatmap = BuildTimeHeatmap(studiedHistory, allCourses);
        result.MonthlyBreakdown = BuildMonthlyBreakdown(studiedHistory, today);
        result.MonthComparison = BuildMonthComparison(studiedHistory, today);
        result.EctsTimeline = BuildEctsTimeline(goals, allCourses);
        result.EctsPlan = BuildEctsPlan(goals, allCourses, settings, result.EctsTotal, today);
        result.ProductivityWeeks = BuildProductivityScore(history, now, today);
        result.GoalHistoryWeeks = BuildGoalHistory(history, settings, now, today);
        result.InactivityWeeks = BuildInactivityTrend(history, now, today);
        result.SessionLengthBuckets = BuildSessionLengthHistogram(history, now);
        result.CourseComparison = BuildCourseComparison(history, now, today);
        result.CourseBalance = BuildCourseBalance(history, allCourses, settings, now);
        return result;
    }

    // ── Phase 2: notes ────────────────────────────────────────────────────────

    /// <summary>Phase 2: the notes-vs-study-time correlation card, deferred behind its own fetch
    /// so a slow notes request never holds back the rest of the page.</summary>
    public static StatsNotesSummaryDto BuildNotes(StatsSummaryInput input)
    {
        var activeCourseIds = ActiveCourseIds(input);
        var history = input.History.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        // General (course-less) notes always stay visible; course-bound ones only for the active
        // programme.
        var notes = input.Notes
            .Where(n => !n.CourseId.HasValue || activeCourseIds.Contains(n.CourseId.Value))
            .ToList();

        return new StatsNotesSummaryDto
        {
            CorrelationWeeks = BuildNotesCorrelation(history, notes, input.Now, input.Today),
        };
    }

    // ── Phase 3: all-time history + every programme's catalog ─────────────────

    /// <summary>Phase 3: the two cards behind the slower fetches - the semester comparison and
    /// the cross-programme comparison.</summary>
    public static StatsExtendedSummaryDto BuildExtended(StatsSummaryInput input)
    {
        var activeCourseIds = ActiveCourseIds(input);
        var allTimeHistory = input.HeavyHistory.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        var goals = input.Goals.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();

        return new StatsExtendedSummaryDto
        {
            SemesterComparison = BuildSemesterComparison(allTimeHistory, goals, input.AllCourses, input.Settings, input.Now),
            ProgramComparison = BuildProgramComparison(input),
        };
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>"3h 20m" - the label shape shared by the totals tile, the semester comparison and
    /// the monthly stacks. Deliberately always spells out the minutes (unlike the dashboard's
    /// variant), because that is what this page has always shown.</summary>
    private static string FormatHoursLabel(double hours) => StudyMetrics.FormatHoursMinutes(hours);

    /// <summary>Shared Monday-start week-bucket helper for the 12-week charts (sparklines,
    /// productivity, goal history, inactivity trend, course comparison, notes correlation) - same
    /// convention as the calendar heatmap (StudyMetrics.WeekStartOf).</summary>
    private static List<DateTime> LastNWeekStarts(int weekCount, DateTime today)
    {
        var currentWeekStart = StudyMetrics.WeekStartOf(today);
        return Enumerable.Range(0, weekCount)
            .Select(i => currentWeekStart.AddDays(-7 * (weekCount - 1 - i)))
            .ToList();
    }

    /// <summary>
    /// Weekly hours for the last 12 weeks per course (same Monday grid as LastNWeekStarts),
    /// normalized to the course's own maximum - the data basis for the mini sparklines in the
    /// course list. Courses without sessions in the window are absent from the dictionary (no
    /// empty sparkline).
    /// </summary>
    private static Dictionary<int, List<double>> BuildCourseSparks(
        List<StudySessionDto> history, DateTime now, DateTime today)
    {
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount, today);
        var windowStart = weekStarts[0];
        var sparks = new Dictionary<int, List<double>>();
        foreach (var group in history
            .Where(s => StudyMetrics.IsStudied(s, now) && s.StartTime.Date >= windowStart)
            .GroupBy(s => s.CourseId))
        {
            var weekly = weekStarts
                .Select(ws => group
                    .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < ws.AddDays(7))
                    .Sum(s => (s.EndTime - s.StartTime).TotalHours))
                .ToList();
            var max = weekly.Max();
            if (max <= 0) continue;
            sparks[group.Key] = weekly.Select(h => h / max * 100).ToList();
        }
        return sparks;
    }

    /// <summary>
    /// Last 30 days vs. the 30 days before that, per course - drives the trend arrows in the
    /// course list. Null (no arrow shown) unless there's at least 1h logged in the prior window
    /// too, so a course only recently started doesn't get a misleading ±100% jump.
    /// </summary>
    private static Dictionary<int, double?> BuildCourseTrends(
        List<StudySessionDto> history, DateTime now, DateTime today)
    {
        double HoursInWindow(int courseId, int fromDaysAgo, int toDaysAgo) => history
            .Where(s => s.CourseId == courseId && StudyMetrics.IsStudied(s, now)
                && s.StartTime.Date > today.AddDays(-fromDaysAgo)
                && s.StartTime.Date <= today.AddDays(-toDaysAgo))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var trends = new Dictionary<int, double?>();
        foreach (var courseId in history.Select(s => s.CourseId).Distinct())
        {
            var priorHours = HoursInWindow(courseId, 60, 30);
            var lastHours = HoursInWindow(courseId, 30, 0);
            trends[courseId] = priorHours >= 1.0 ? (lastHours - priorHours) / priorHours * 100 : (double?)null;
        }
        return trends;
    }

    /// <summary>
    /// Course ECTS are all-or-nothing - for ongoing courses, the topic progress (setup checklist,
    /// same CompletedTopics semantics as the dashboard) fills the ring as an interim state, so it
    /// isn't just binary empty/full.
    /// </summary>
    private static double CalcEctsRingPercent(CourseDto course, bool isCompleted, CourseGoalDto? goal)
    {
        if (isCompleted) return 100;
        if (course.Topics.Count == 0) return 0;
        var completedTopics = string.IsNullOrWhiteSpace(goal?.CompletedTopics)
            ? new HashSet<string>()
            : goal.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return course.Topics.Count(t => completedTopics.Contains(t)) / (double)course.Topics.Count * 100;
    }

    // ── Grades ────────────────────────────────────────────────────────────────

    private static List<StatsGradeBucketDto> BuildGradeDistribution(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Half-grade bands of the German scale (1.0 = best grade), "> 4.0" = failed.
        // Same catalog join as the grade history (BuildGradeHistory): a graded course with no
        // catalog entry has to be treated identically by both cards, otherwise the distribution
        // counted a grade the semester chart right next to it silently dropped.
        var catalogIds = allCourses.Select(c => c.Id).ToHashSet();
        var buckets = new (string Label, decimal UpTo)[]
        {
            ("1,0–1,5", 1.5m), ("1,6–2,0", 2.0m), ("2,1–2,5", 2.5m),
            ("2,6–3,0", 3.0m), ("3,1–3,5", 3.5m), ("3,6–4,0", 4.0m), ("> 4,0", decimal.MaxValue),
        };
        var counts = new int[buckets.Length];
        foreach (var g in goals)
        {
            if (!g.Grade.HasValue || !catalogIds.Contains(g.CourseId)) continue;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (g.Grade.Value <= buckets[i].UpTo) { counts[i]++; break; }
            }
        }
        var max = Math.Max(1, counts.Max());
        return buckets
            .Select((b, i) => new StatsGradeBucketDto { Label = b.Label, Count = counts[i], Percent = counts[i] / (double)max * 100 })
            .ToList();
    }

    private static List<StatsSemesterGradeDto> BuildGradeHistory(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Same weighting semantics as the average grade (Grade × Ects / sum of Ects, falling back
        // to an unweighted mean when the Ects sum is 0), just grouped per semester. Graded courses
        // without a catalog entry drop out here (no semester can be assigned).
        var groups = goals
            .Where(g => g.Grade.HasValue)
            .Select(g => (Grade: g.Grade!.Value, Course: allCourses.FirstOrDefault(c => c.Id == g.CourseId)))
            .Where(x => x.Course != null)
            .GroupBy(x => x.Course!.Semester)
            .OrderBy(g => g.Key)
            .ToList();

        return groups
            .Select(g =>
            {
                // Group is never empty (GroupBy) -> CalcWeightedAverageGrade never returns null here.
                var avg = StudyMetrics.CalcWeightedAverageGrade(g.Select(x => new StudyMetrics.GradedCourse(x.Grade, x.Course!.Ects)))!.Value;
                // German grading scale: 1.0 = best grade -> inverted, so better grades yield taller bars.
                var percent = Math.Clamp((5.0 - (double)avg) / 4.0 * 100, 0, 100);
                return new StatsSemesterGradeDto { Semester = g.Key, AvgGrade = avg, CourseCount = g.Count(), BarPercent = percent };
            })
            .ToList();
    }

    private static List<StatsGradeTimelinePointDto> BuildGradeTimeline(List<CourseGoalDto> goals, List<CourseDto> allCourses) =>
        // Individual grades in chronological order by actual completion date - deliberately
        // WITHOUT BuildGradeHistory's semester grouping (average per catalog semester there) and
        // without requiring a catalog entry: a grade here only needs a CompletedAt, no assignable
        // semester.
        goals
            .Where(g => g.Grade.HasValue && g.CompletedAt.HasValue)
            .OrderBy(g => g.CompletedAt!.Value)
            .Select(g => new StatsGradeTimelinePointDto
            {
                Date = g.CompletedAt!.Value,
                CourseName = g.CourseName,
                Color = allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Color ?? "#888888",
                Grade = g.Grade!.Value,
                // Inverted scale like BuildGradeHistory: better grades yield taller columns.
                BarPercent = Math.Clamp((5.0 - (double)g.Grade!.Value) / 4.0 * 100, 0, 100),
            })
            .ToList();

    /// <summary>Record struct instead of a value tuple for the materialized `points` list below -
    /// LINQ over a List&lt;(...)&gt; of value tuples has triggered a Mono AOT crash at compile
    /// time (not call time) in the native app shell (studylife-app, BlazorWebView) that links the
    /// Client project this code came from - see project_studylife_app_ios_aot_linq_tuple_crash.</summary>
    private readonly record struct HoursGradePoint(CourseDto Course, decimal Grade, double Hours);

    private static StatsHoursGradeScatterDto BuildHoursGradeScatter(
        List<CourseGoalDto> goals, List<CourseDto> allCourses, List<StudyMetrics.CourseHoursResult> perCourseHours)
    {
        // "Does more studying pay off?": hours per course (same source as the course rows, the
        // `raw` aggregate from the session list) against the grade achieved. Graded courses
        // without logged hours deliberately appear at x=0 instead of disappearing.
        var points = goals
            .Where(g => g.Grade.HasValue)
            .Select(g => (Grade: g.Grade!.Value, Course: allCourses.FirstOrDefault(c => c.Id == g.CourseId)))
            .Where(x => x.Course != null)
            .Select(x => new HoursGradePoint(x.Course!, x.Grade, perCourseHours.FirstOrDefault(r => r.Course.Id == x.Course!.Id).Hours))
            .ToList();

        var maxHours = Math.Max(1.0, points.Count == 0 ? 0 : Math.Ceiling(points.Max(p => p.Hours)));

        // 5% margin on both axes, so edge points (x=0, grade 1.0/5.0) aren't clipped at the card
        // border. Y inverted like the grade history: (5.0 − grade) / 4.0, better grades sit higher.
        return new StatsHoursGradeScatterDto
        {
            MaxHoursLabel = $"{(int)maxHours}h",
            Points = points
                .Select(p => new StatsHoursGradePointDto
                {
                    Name = p.Course.Name,
                    Icon = p.Course.Icon,
                    Color = p.Course.Color,
                    Hours = p.Hours,
                    Grade = p.Grade,
                    XPercent = 5 + Math.Clamp(p.Hours / maxHours, 0, 1) * 90,
                    YPercent = 5 + Math.Clamp((5.0 - (double)p.Grade) / 4.0, 0, 1) * 90,
                })
                .ToList(),
        };
    }

    /// <summary>Record struct instead of a value tuple - see <see cref="HoursGradePoint"/>.</summary>
    private readonly record struct HoursEctsPoint(CourseDto Course, double Hours, int EctsEarned);

    private static StatsHoursEctsScatterDto BuildHoursEctsScatter(
        List<CourseDto> allCourses, List<StudyMetrics.CourseHoursResult> perCourseHours, UserSettingsDto settings)
    {
        // "What did the time actually earn?": hours per course (same raw aggregate as the
        // hours-vs-grade scatter) against the ECTS harvested. Course ECTS are all-or-nothing,
        // so ongoing courses sit at y=0 (invested, nothing harvested yet) and completed courses
        // without logged hours sit at x=0 - both deliberately visible.
        var candidates = perCourseHours.Select(r => r.Course)
            .Concat(settings.CompletedCourseIds
                .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Select(c => c!))
            .DistinctBy(c => c.Id)
            .ToList();

        var points = candidates
            .Select(c => new HoursEctsPoint(
                c,
                perCourseHours.FirstOrDefault(r => r.Course.Id == c.Id).Hours,
                settings.CompletedCourseIds.Contains(c.Id) ? c.Ects : 0))
            .ToList();

        var maxHours = Math.Max(1.0, points.Count == 0 ? 0 : Math.Ceiling(points.Max(p => p.Hours)));
        var maxEcts = Math.Max(1, points.Count == 0 ? 0 : points.Max(p => p.Course.Ects));

        // 5% margin on both axes like the hours-vs-grade scatter, so edge points
        // (x=0, y=0) aren't clipped at the card border.
        return new StatsHoursEctsScatterDto
        {
            MaxHoursLabel = $"{(int)maxHours}h",
            MaxEctsLabel = maxEcts.ToString(),
            Points = points
                .Select(p => new StatsHoursEctsPointDto
                {
                    Name = p.Course.Name,
                    Icon = p.Course.Icon,
                    Color = p.Course.Color,
                    Hours = p.Hours,
                    EctsEarned = p.EctsEarned,
                    XPercent = 5 + Math.Clamp(p.Hours / maxHours, 0, 1) * 90,
                    YPercent = 5 + Math.Clamp(p.EctsEarned / (double)maxEcts, 0, 1) * 90,
                })
                .ToList(),
        };
    }

    // ── Charts ────────────────────────────────────────────────────────────────

    private static StatsHeatmapDto BuildHeatmap(List<StudySessionDto> history, List<CourseDto> allCourses, DateTime today)
    {
        const int totalWeeks = 53;
        var hoursByDate = history
            .GroupBy(s => s.StartTime.Date)
            .ToDictionary(g => g.Key, g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours));
        // Per-course breakdown per day for the click popover - separate from hoursByDate because
        // most days (level 0/-1) don't need it at all and we want to save the GroupBy cost.
        var byDateAndCourse = history
            .GroupBy(s => s.StartTime.Date)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.CourseId)
                    .Select(cg => new StatsCourseHoursDto
                    {
                        CourseId = cg.Key,
                        Color = allCourses.FirstOrDefault(x => x.Id == cg.Key)?.Color ?? "#888888",
                        Hours = cg.Sum(s => (s.EndTime - s.StartTime).TotalHours),
                    })
                    .OrderByDescending(c => c.Hours)
                    .ToList());
        var sessionCountByDate = history.GroupBy(s => s.StartTime.Date).ToDictionary(g => g.Key, g => g.Count());

        var currentWeekStart = StudyMetrics.WeekStartOf(today);
        var gridStart = currentWeekStart.AddDays(-7 * (totalWeeks - 1));

        var heatmap = new StatsHeatmapDto();
        var lastMonth = -1;
        for (var w = 0; w < totalWeeks; w++)
        {
            var weekStart = gridStart.AddDays(7 * w);
            var days = new List<StatsHeatDayDto>();
            for (var d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                if (date > today)
                {
                    days.Add(new StatsHeatDayDto { Date = date, Hours = 0, Level = -1, SessionCount = 0 });
                    continue;
                }
                var hours = hoursByDate.TryGetValue(date, out var h) ? h : 0;
                // Bands are inclusive at the top (<= 1, <= 2, <= 4): with exclusive bounds a
                // round 1.0 h jumped a whole level above 0.99 h, so the neatest days - the ones
                // logged as whole hours - were consistently shaded darker than they earned.
                var level = hours <= 0 ? 0 : hours <= 1 ? 1 : hours <= 2 ? 2 : hours <= 4 ? 3 : 4;
                var sessionCount = sessionCountByDate.TryGetValue(date, out var sc) ? sc : 0;
                var courses = byDateAndCourse.TryGetValue(date, out var cs) ? cs : new();
                days.Add(new StatsHeatDayDto
                {
                    Date = date,
                    Hours = hours,
                    Level = level,
                    SessionCount = sessionCount,
                    Courses = courses,
                });
            }
            heatmap.Weeks.Add(new StatsHeatmapWeekDto
            {
                WeekStart = weekStart,
                ShowMonthLabel = weekStart.Month != lastMonth,
                Days = days,
            });
            lastMonth = weekStart.Month;
        }
        return heatmap;
    }

    private static StatsDonutDto BuildDonut(List<StudySessionDto> history, List<CourseDto> allCourses, DateTime today)
    {
        var byCourse = history
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Sessions: g.ToList(), Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .Where(x => x.Hours > 0)
            .OrderByDescending(x => x.Hours)
            .ToList();

        var total = byCourse.Sum(x => x.Hours);
        var donut = new StatsDonutDto { TotalHours = total };
        if (total <= 0) return donut;

        // Embed the monthly mini-chart + recent sessions for the click drilldown directly in the
        // slice (same idea as BuildHeatmap's byDateAndCourse: the card gets everything up front
        // instead of having to reload on click).
        const int monthCount = 12;
        const int recentSessionCount = 8;
        var monthStarts = Enumerable.Range(0, monthCount)
            .Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(-(monthCount - 1 - i)))
            .ToList();

        donut.Slices = byCourse
            .Select(x =>
            {
                var perMonth = monthStarts
                    .Select(m => x.Sessions
                        .Where(s => s.StartTime.Year == m.Year && s.StartTime.Month == m.Month)
                        .Sum(s => (s.EndTime - s.StartTime).TotalHours))
                    .ToList();
                // Scale relative to THIS course's strongest month - the drilldown shows a
                // course's rhythm, not a cross-course comparison (that's what the monthly
                // breakdown is for).
                var maxMonth = perMonth.Max();
                var months = monthStarts
                    .Select((m, i) => new StatsDonutMonthDto
                    {
                        MonthStart = m,
                        Hours = perMonth[i],
                        Percent = maxMonth > 0 ? perMonth[i] / maxMonth * 100 : 0,
                    })
                    .ToList();
                var recent = x.Sessions
                    .OrderByDescending(s => s.StartTime)
                    .Take(recentSessionCount)
                    .Select(s => new StatsDonutSessionDto { Start = s.StartTime, End = s.EndTime, Topic = s.Topic })
                    .ToList();
                return new StatsDonutSliceDto
                {
                    CourseId = x.CourseId,
                    Color = allCourses.FirstOrDefault(c => c.Id == x.CourseId)?.Color ?? "#888888",
                    Hours = x.Hours,
                    Percent = x.Hours / total * 100,
                    SessionCount = x.Sessions.Count,
                    Months = months,
                    RecentSessions = recent,
                };
            })
            .ToList();

        var parts = new List<string>();
        var cursor = 0.0;
        foreach (var s in donut.Slices)
        {
            var start = cursor;
            var end = cursor + s.Percent;
            parts.Add($"{s.Color} {start.ToString("0.###", CultureInfo.InvariantCulture)}% {end.ToString("0.###", CultureInfo.InvariantCulture)}%");
            cursor = end;
        }
        donut.Gradient = "conic-gradient(" + string.Join(", ", parts) + ")";
        return donut;
    }

    private static StatsRhythmDto BuildRhythm(List<StudySessionDto> history)
    {
        var hoursByWeekday = new double[7];
        foreach (var s in history)
        {
            var idx = ((int)s.StartTime.DayOfWeek + 6) % 7; // Monday = 0
            hoursByWeekday[idx] += (s.EndTime - s.StartTime).TotalHours;
        }

        var buckets = new (string Label, int From, int To)[]
        {
            ("00-06", 0, 6), ("06-09", 6, 9), ("09-12", 9, 12), ("12-15", 12, 15),
            ("15-18", 15, 18), ("18-21", 18, 21), ("21-24", 21, 24),
        };
        var hoursByBucket = new double[buckets.Length];
        foreach (var s in history)
        {
            var hour = s.StartTime.Hour;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (hour >= buckets[i].From && hour < buckets[i].To)
                {
                    hoursByBucket[i] += (s.EndTime - s.StartTime).TotalHours;
                    break;
                }
            }
        }
        var maxBucket = Math.Max(1, hoursByBucket.Max());
        var maxWeekday = Math.Max(1, hoursByWeekday.Max());

        return new StatsRhythmDto
        {
            WeekdayHours = hoursByWeekday.ToList(),
            WeekdayMax = maxWeekday,
            TimeOfDay = buckets
                .Select((b, i) => new StatsBarPointDto
                {
                    Label = b.Label,
                    Hours = hoursByBucket[i],
                    Percent = Math.Min(100, hoursByBucket[i] / maxBucket * 100),
                })
                .ToList(),
        };
    }

    private static StatsTimeHeatmapDto BuildTimeHeatmap(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        // Same "attribute the whole session to its start hour" bucketing BuildRhythm already uses
        // (no minute-by-minute splitting across hour boundaries) - kept consistent with that
        // sibling rather than introducing a more precise but inconsistent approach.
        var hoursByCell = new double[7, 24];
        var sessionCountByCell = new int[7, 24];
        // Per-course breakdown per cell for the click detail panel - same idea as BuildHeatmap's
        // byDateAndCourse, just with (weekday, hour) instead of date as the key.
        var byCellAndCourse = new Dictionary<(int Weekday, int Hour), Dictionary<int, double>>();
        foreach (var s in history)
        {
            var weekdayIdx = ((int)s.StartTime.DayOfWeek + 6) % 7; // Monday = 0
            var sessionHours = (s.EndTime - s.StartTime).TotalHours;
            hoursByCell[weekdayIdx, s.StartTime.Hour] += sessionHours;
            sessionCountByCell[weekdayIdx, s.StartTime.Hour]++;
            var key = (weekdayIdx, s.StartTime.Hour);
            if (!byCellAndCourse.TryGetValue(key, out var perCourse))
                byCellAndCourse[key] = perCourse = new();
            perCourse[s.CourseId] = perCourse.GetValueOrDefault(s.CourseId) + sessionHours;
        }

        // Unlike BuildHeatmap's per-CALENDAR-DAY levels (fixed <1/<2/<4h cutoffs make sense there,
        // since one day tops out around a handful of hours), a weekday+hour cell here sums up to
        // ~53 occurrences of that slot across the whole history window. Reusing those same absolute
        // cutoffs would push nearly every regularly-used slot straight to the top level and erase
        // the pattern this chart exists to show - so levels are relative to this grid's own max cell
        // instead, split into quarters of that max (still the same 5-level look/CSS as the other heatmap).
        var maxCell = 1.0;
        foreach (var h in hoursByCell)
            if (h > maxCell) maxCell = h;

        var result = new StatsTimeHeatmapDto { MaxCell = maxCell };
        for (var w = 0; w < 7; w++)
        {
            var hourRow = new List<double>();
            var countRow = new List<int>();
            for (var h = 0; h < 24; h++)
            {
                hourRow.Add(hoursByCell[w, h]);
                countRow.Add(sessionCountByCell[w, h]);
            }
            result.HoursByCell.Add(hourRow);
            result.SessionCountByCell.Add(countRow);
        }

        // Ordered here rather than at render time (the client used to sort the dictionary on every
        // rebuild) - same sequence either way, since OrderByDescending is stable and the source
        // order is the dictionary's insertion order in both cases.
        foreach (var (key, perCourse) in byCellAndCourse)
        {
            result.CellCourses.Add(new StatsTimeHeatmapCellDto
            {
                Weekday = key.Weekday,
                Hour = key.Hour,
                Courses = perCourse
                    .Select(kv => new StatsCourseHoursDto
                    {
                        CourseId = kv.Key,
                        Color = allCourses.FirstOrDefault(c => c.Id == kv.Key)?.Color ?? "#888888",
                        Hours = kv.Value,
                    })
                    .OrderByDescending(c => c.Hours)
                    .ToList(),
            });
        }
        return result;
    }

    private static StatsMonthlyBreakdownDto BuildMonthlyBreakdown(List<StudySessionDto> history, DateTime today)
    {
        const int monthCount = 6;
        var monthStarts = Enumerable.Range(0, monthCount)
            .Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(-(monthCount - 1 - i)))
            .ToList();

        var perMonthCourseHours = monthStarts.Select(_ => new Dictionary<int, double>()).ToList();
        foreach (var s in history)
        {
            var monthStart = new DateTime(s.StartTime.Year, s.StartTime.Month, 1);
            var idx = monthStarts.FindIndex(m => m == monthStart);
            if (idx < 0) continue;
            var dict = perMonthCourseHours[idx];
            dict[s.CourseId] = dict.GetValueOrDefault(s.CourseId) + (s.EndTime - s.StartTime).TotalHours;
        }

        var totalsByCourse = new Dictionary<int, double>();
        foreach (var dict in perMonthCourseHours)
            foreach (var (courseId, hours) in dict)
                totalsByCourse[courseId] = totalsByCourse.GetValueOrDefault(courseId) + hours;

        var orderedIds = totalsByCourse.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();

        return new StatsMonthlyBreakdownDto
        {
            MonthStarts = monthStarts,
            PerMonthCourseHours = perMonthCourseHours,
            OrderedIds = orderedIds,
            TopIds = orderedIds.Take(6).ToList(),
            MaxMonthTotal = Math.Max(1, perMonthCourseHours.Select(d => d.Values.Sum()).DefaultIfEmpty(0).Max()),
        };
    }

    // ── Trends ────────────────────────────────────────────────────────────────

    private static StatsForecastDto BuildForecast(
        UserSettingsDto settings, List<CourseDto> allCourses, List<StudySessionDto> history,
        int ectsTotal, int ectsEarned, DateTime now)
    {
        // Formula and guards: see StudyMetrics.CalcForecast (shared with the dashboard).
        var forecast = StudyMetrics.CalcForecast(ectsTotal, ectsEarned, allCourses,
            settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, history, now);
        return new StatsForecastDto
        {
            Available = forecast.Available,
            AlreadyDone = forecast.AlreadyDone,
            DateLabel = forecast.Available ? forecast.ForecastDate!.Value.ToString("dd.MM.yyyy") : "",
        };
    }

    private static StatsMonthComparisonDto BuildMonthComparison(List<StudySessionDto> history, DateTime today)
    {
        double HoursInMonth(int year, int month) => history
            .Where(s => s.StartTime.Year == year && s.StartTime.Month == month)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var thisMonthHours = HoursInMonth(today.Year, today.Month);
        var lastMonthDate = today.AddMonths(-1);
        var lastMonthHours = HoursInMonth(lastMonthDate.Year, lastMonthDate.Month);

        var delta = thisMonthHours - lastMonthHours;
        return new StatsMonthComparisonDto
        {
            Up = delta >= 0,
            DeltaLabel = StudyMetrics.FormatHoursMinutes(Math.Abs(delta), omitZeroMinutes: true),
        };
    }

    /// <summary>Record struct instead of a value tuple for the materialized `raw` list below -
    /// see <see cref="HoursGradePoint"/>.</summary>
    private readonly record struct EctsTimelineRaw(DateTime Date, int Cumulative);

    private static List<StatsEctsTimelinePointDto> BuildEctsTimeline(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Cumulative ECTS over time ("progress timeline"): each point is a completed goal with
        // a date, height = sum of ECTS of all courses completed up to and including that point.
        // Goals without CompletedAt (e.g. only graded, never marked "completed") deliberately
        // don't show up here - unlike the average grade.
        var completed = goals.Where(g => g.CompletedAt.HasValue).OrderBy(g => g.CompletedAt!.Value).ToList();
        if (completed.Count < 2) return new List<StatsEctsTimelinePointDto>();

        var running = 0;
        var raw = new List<EctsTimelineRaw>();
        foreach (var g in completed)
        {
            var ects = allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5;
            running += ects;
            raw.Add(new EctsTimelineRaw(g.CompletedAt!.Value, running));
        }
        var max = Math.Max(1, raw.Max(r => r.Cumulative));
        return raw
            .Select(r => new StatsEctsTimelinePointDto
            {
                Date = r.Date,
                CumulativeEcts = r.Cumulative,
                Percent = r.Cumulative / (double)max * 100,
            })
            .ToList();
    }

    /// <summary>Record struct instead of a value tuple - see <see cref="HoursGradePoint"/>.</summary>
    private readonly record struct EctsPlanCompletion(DateTime Date, int Ects);

    /// <summary>
    /// Actual-vs-target ECTS progression: actual = cumulative ECTS of completed goals (same data
    /// basis as <see cref="BuildEctsTimeline"/>), target = linear trajectory from the first
    /// completion to the desired graduation date (UserSettingsDto.TargetGraduationDate) reaching
    /// <paramref name="ectsTotal"/> - the same "spread remaining effort evenly over the remaining
    /// time" idea as the dashboard's graduation-goal card, just in ECTS instead of weekly hours.
    /// Monthly grid, horizontally scrollable like the other wide charts on this page.
    /// </summary>
    private static List<StatsEctsPlanPointDto> BuildEctsPlan(
        List<CourseGoalDto> goals, List<CourseDto> allCourses, UserSettingsDto settings, int ectsTotal, DateTime today)
    {
        var points = new List<StatsEctsPlanPointDto>();
        if (!settings.TargetGraduationDate.HasValue || ectsTotal <= 0) return points;

        var completed = goals
            .Where(g => g.CompletedAt.HasValue)
            .OrderBy(g => g.CompletedAt!.Value)
            .Select(g => new EctsPlanCompletion(g.CompletedAt!.Value.Date, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5))
            .ToList();
        if (completed.Count == 0) return points;

        var startDate = completed[0].Date;
        var targetDate = settings.TargetGraduationDate.Value.Date;
        // Target date before the first completion: no meaningful target line can be constructed.
        if (targetDate <= startDate) return points;

        var startMonth = new DateTime(startDate.Year, startDate.Month, 1);
        var endDate = targetDate > today ? targetDate : today;
        var endMonth = new DateTime(endDate.Year, endDate.Month, 1);
        // Absurd target dates (> 10 years span) would generate hundreds of columns - better to
        // show the empty state than a mile-long, meaningless chart.
        if (((endMonth.Year - startMonth.Year) * 12 + endMonth.Month - startMonth.Month) > 120) return points;

        // Actual can exceed the target end value (e.g. extra courses beyond the quotas) -
        // the scale takes the maximum of both series so nothing gets clipped.
        var scaleMax = Math.Max(ectsTotal, completed.Sum(c => c.Ects));
        var totalPlanDays = (targetDate - startDate).TotalDays;

        for (var m = startMonth; m <= endMonth; m = m.AddMonths(1))
        {
            var monthEnd = m.AddMonths(1).AddDays(-1);
            int? actualEcts = m <= today
                ? completed.Where(c => c.Date <= monthEnd).Sum(c => c.Ects)
                : null;
            var target = (int)Math.Round(Math.Clamp((monthEnd - startDate).TotalDays / totalPlanDays, 0, 1) * ectsTotal);
            points.Add(new StatsEctsPlanPointDto
            {
                Label = m.ToString("MM.yy"),
                ActualEcts = actualEcts,
                ActualPercent = actualEcts.HasValue ? Math.Min(100, actualEcts.Value / (double)scaleMax * 100) : null,
                TargetEcts = target,
                TargetPercent = Math.Min(100, target / (double)scaleMax * 100),
            });
        }
        return points;
    }

    private static List<StatsProductivityWeekDto> BuildProductivityScore(
        List<StudySessionDto> history, DateTime now, DateTime today)
    {
        // "Productivity/engagement score": StudySessionDto has no separate planned vs. actual
        // duration (Start/EndTime ARE the entry) - instead, per week, the share of "studied"
        // sessions (IsCompleted || time elapsed) that were actively completed via the focus
        // timer (IsCompleted), rather than just having elapsed. Weeks with no studied sessions
        // at all get Percent=null (no misleading 0% bar).
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount, today);
        return weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var studied = history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, now))
                .ToList();
            if (studied.Count == 0)
                return new StatsProductivityWeekDto { Label = ws.ToString("dd.MM."), Percent = null };
            var completedCount = studied.Count(s => s.IsCompleted);
            return new StatsProductivityWeekDto
            {
                Label = ws.ToString("dd.MM."),
                Percent = completedCount / (double)studied.Count * 100,
            };
        }).ToList();
    }

    private static List<StatsGoalHistoryWeekDto> BuildGoalHistory(
        List<StudySessionDto> history, UserSettingsDto settings, DateTime now, DateTime today)
    {
        // Last 12 weeks: reached when the weekly hours >= WeeklyGoalMinHours (same threshold
        // as the weekly quota on the dashboard).
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount, today);
        return weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var hours = history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, now))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
            return new StatsGoalHistoryWeekDto { WeekStart = ws, Met = hours >= settings.WeeklyGoalMinHours, Hours = hours };
        }).ToList();
    }

    private static List<StatsInactivityWeekDto> BuildInactivityTrend(
        List<StudySessionDto> history, DateTime now, DateTime today)
    {
        // Continuous hours/week as a bar chart - deliberately separate from the goal history
        // above (there, binary reached/missed as a point series), so a gradual decline
        // (even while still above the weekly goal) becomes visible.
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount, today);
        var weekHours = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            return history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, now))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        }).ToList();

        var maxHours = Math.Max(1, weekHours.DefaultIfEmpty(0).Max());
        return weekStarts
            .Select((ws, i) => new StatsInactivityWeekDto
            {
                Label = ws.ToString("dd.MM."),
                Hours = weekHours[i],
                Percent = Math.Min(100, weekHours[i] / maxHours * 100),
            })
            .ToList();
    }

    private static List<StatsLengthBucketDto> BuildSessionLengthHistogram(List<StudySessionDto> history, DateTime now)
    {
        var buckets = new (string Label, double FromMin, double ToMin)[]
        {
            ("<30m", 0, 30), ("30-60m", 30, 60), ("60-90m", 60, 90), ("90-120m", 90, 120), ("120m+", 120, double.MaxValue),
        };
        var counts = new int[buckets.Length];
        foreach (var s in history)
        {
            if (!StudyMetrics.IsStudied(s, now)) continue;
            var minutes = (s.EndTime - s.StartTime).TotalMinutes;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (minutes >= buckets[i].FromMin && minutes < buckets[i].ToMin) { counts[i]++; break; }
            }
        }
        var max = Math.Max(1, counts.Max());
        return buckets
            .Select((b, i) => new StatsLengthBucketDto { Label = b.Label, Count = counts[i], Percent = counts[i] / (double)max * 100 })
            .ToList();
    }

    // ── Comparisons ───────────────────────────────────────────────────────────

    /// <summary>
    /// Multiple courses as a grouped bar chart (one cluster per week, one bar per course)
    /// over the last 12 weeks - "am I currently studying course A more than course B?" at a
    /// glance, unlike the plain per-course aggregates of the course list. Limited to the top 5
    /// courses (by hours within the 12-week window), so the legend doesn't turn into an
    /// unreadable rainbow.
    /// </summary>
    private static StatsCourseComparisonDto BuildCourseComparison(
        List<StudySessionDto> history, DateTime now, DateTime today)
    {
        const int weekCount = 12;
        const int maxCourses = 5;
        var weekStarts = LastNWeekStarts(weekCount, today);
        var windowStart = weekStarts[0];

        var studied = history
            .Where(s => StudyMetrics.IsStudied(s, now) && s.StartTime.Date >= windowStart)
            .ToList();

        var topCourseIds = studied
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .Take(maxCourses)
            .Select(x => x.CourseId)
            .ToList();

        // Nothing studied in the window - the card shows its empty state, no scale is needed.
        if (topCourseIds.Count == 0) return new StatsCourseComparisonDto();

        var perWeekPerCourse = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            return topCourseIds.ToDictionary(id => id, id => studied
                .Where(s => s.CourseId == id && s.StartTime.Date >= ws && s.StartTime.Date < we)
                .Sum(s => (s.EndTime - s.StartTime).TotalHours));
        }).ToList();

        return new StatsCourseComparisonDto
        {
            TopCourseIds = topCourseIds,
            WeekStarts = weekStarts,
            PerWeekPerCourse = perWeekPerCourse,
            // A single shared scale across all weeks/courses (instead of rescaling per week),
            // so bar heights remain genuinely comparable between weeks and courses.
            MaxHours = Math.Max(1.0, perWeekPerCourse.SelectMany(d => d.Values).DefaultIfEmpty(0).Max()),
        };
    }

    /// <summary>
    /// Notes activity vs. study time per week, last 12 weeks (same weekly grid as everywhere
    /// else in this builder) - a quick "do the two move together?" glance, not a real correlation
    /// analysis. With too few notes (fewer than <see cref="NotesCorrelationMinNotes"/> within the
    /// window) the result stays empty and the card shows an empty state instead of a misleadingly
    /// sparse chart.
    /// </summary>
    private static List<StatsNotesCorrelationWeekDto> BuildNotesCorrelation(
        List<StudySessionDto> history, List<NoteDto> notes, DateTime now, DateTime today)
    {
        const int weekCount = 12;
        const int minNotesInWindow = NotesCorrelationMinNotes;
        var weekStarts = LastNWeekStarts(weekCount, today);
        var windowStart = weekStarts[0];

        var notesInWindow = notes.Where(n => n.CreatedAt.Date >= windowStart).ToList();
        if (notesInWindow.Count < minNotesInWindow) return new List<StatsNotesCorrelationWeekDto>();

        var studied = history.Where(s => StudyMetrics.IsStudied(s, now)).ToList();

        var weekData = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var notesCount = notesInWindow.Count(n => n.CreatedAt.Date >= ws && n.CreatedAt.Date < we);
            var hours = studied
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we)
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
            return (NotesCount: notesCount, Hours: hours);
        }).ToList();

        var maxNotes = Math.Max(1, weekData.Max(w => w.NotesCount));
        var maxHours = Math.Max(1.0, weekData.Max(w => w.Hours));

        return weekStarts
            .Select((ws, i) => new StatsNotesCorrelationWeekDto
            {
                Label = ws.ToString("dd.MM."),
                NotesCount = weekData[i].NotesCount,
                NotesPercent = weekData[i].NotesCount / (double)maxNotes * 100,
                Hours = weekData[i].Hours,
                HoursPercent = Math.Min(100, weekData[i].Hours / maxHours * 100),
            })
            .ToList();
    }

    /// <summary>
    /// Current semester directly against the average of all previous semesters (study hours,
    /// average grade, ECTS as a pace measure), semester bucketing via CourseDto.Semester as in
    /// <see cref="BuildGradeHistory"/>. Needs the ALL-TIME history instead of the
    /// <see cref="HistoryDays"/>-day one: sessions from earlier semesters mostly fall outside that
    /// window and would otherwise systematically push their hours average toward zero.
    /// </summary>
    private static StatsSemesterComparisonDto BuildSemesterComparison(
        List<StudySessionDto> allTimeHistory, List<CourseGoalDto> goals, List<CourseDto> allCourses,
        UserSettingsDto settings, DateTime now)
    {
        var result = new StatsSemesterComparisonDto();

        var semesterByCourse = allCourses.ToDictionary(c => c.Id, c => c.Semester);

        // Current semester = most common semester among the active (selected, not completed)
        // courses - more robust than the maximum, which a single course taken ahead of schedule
        // would skew. Ties: the more advanced semester.
        var activeSemesters = settings.SelectedCourseIds
            .Except(settings.CompletedCourseIds)
            .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null)
            .Select(c => c!.Semester)
            .ToList();
        if (activeSemesters.Count == 0) return result;
        var currentSemester = activeSemesters
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;

        double HoursOf(int semester) => allTimeHistory
            .Where(s => StudyMetrics.IsStudied(s, now)
                && semesterByCourse.TryGetValue(s.CourseId, out var sem) && sem == semester)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        decimal? GradeOf(int semester) => StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue && semesterByCourse.TryGetValue(g.CourseId, out var sem) && sem == semester)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.First(c => c.Id == g.CourseId).Ects)));
        int EctsOf(int semester) => allCourses
            .Where(c => c.Semester == semester && settings.CompletedCourseIds.Contains(c.Id))
            .Sum(c => c.Ects);

        // Previous semesters only count if they have any data at all - a completely empty
        // semester (lateral entry, leave of absence) would otherwise dilute the average for no reason.
        var previous = allCourses.Select(c => c.Semester)
            .Where(s => s < currentSemester)
            .Distinct()
            .Select(s => (Hours: HoursOf(s), Grade: GradeOf(s), Ects: EctsOf(s)))
            .Where(x => x.Hours > 0 || x.Grade.HasValue || x.Ects > 0)
            .ToList();
        if (previous.Count == 0) return result;

        var curHours = HoursOf(currentSemester);
        var curGrade = GradeOf(currentSemester);
        var curEcts = EctsOf(currentSemester);
        var prevHours = previous.Average(p => p.Hours);
        // Average of the semester averages (each previous semester counts equally), not an
        // ECTS-weighted overall average - it's semester compared against semester.
        var prevGrades = previous.Where(p => p.Grade.HasValue).Select(p => (double)p.Grade!.Value).ToList();
        double? prevGrade = prevGrades.Count > 0 ? prevGrades.Average() : null;
        var prevEcts = previous.Average(p => (double)p.Ects);

        result.HasData = true;
        result.CurrentHours = curHours;
        result.CurrentGrade = curGrade;
        result.CurrentEcts = curEcts;
        result.PreviousHours = prevHours;
        result.PreviousGrade = prevGrade;
        result.PreviousEcts = prevEcts;
        result.CurrentSemester = currentSemester;
        result.MaxHoursScale = Math.Max(1.0, Math.Max(curHours, prevHours));
        result.MaxEctsScale = Math.Max(1.0, Math.Max(curEcts, prevEcts));
        return result;
    }

    /// <summary>
    /// ECTS-weighted target time share vs. actual time share per active course - which courses
    /// get more/less study time than would be "fair" relative to their credit weight? Only
    /// selected, not-yet-completed courses (same active definition as the IsCompleted flag of the
    /// course rows) - completed courses would otherwise needlessly skew the target share without
    /// any further invested time making sense.
    /// </summary>
    private static List<StatsCourseBalanceRowDto> BuildCourseBalance(
        List<StudySessionDto> history, List<CourseDto> allCourses, UserSettingsDto settings, DateTime now)
    {
        var activeIds = settings.SelectedCourseIds.Except(settings.CompletedCourseIds).ToList();
        var activeCourses = activeIds
            .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();

        if (activeCourses.Count == 0) return new List<StatsCourseBalanceRowDto>();

        var totalEcts = activeCourses.Sum(c => c.Ects);
        var studied = history.Where(s => StudyMetrics.IsStudied(s, now) && activeIds.Contains(s.CourseId)).ToList();
        var hoursByCourse = activeIds.ToDictionary(id => id, id => studied.Where(s => s.CourseId == id).Sum(s => (s.EndTime - s.StartTime).TotalHours));
        var totalHours = hoursByCourse.Values.Sum();

        return activeCourses
            .Select(c =>
            {
                var targetPercent = totalEcts > 0 ? c.Ects / (double)totalEcts * 100 : 0;
                var actualPercent = totalHours > 0 ? hoursByCourse[c.Id] / totalHours * 100 : 0;
                return new StatsCourseBalanceRowDto
                {
                    Name = c.Name,
                    Icon = c.Icon,
                    Color = c.Color,
                    TargetPercent = targetPercent,
                    ActualPercent = actualPercent,
                };
            })
            // Largest deviation first - the most interesting rows (most over-/under-invested) on top.
            .OrderByDescending(r => Math.Abs(r.ActualPercent - r.TargetPercent))
            .ToList();
    }

    /// <summary>
    /// Programmes side by side (hours, ECTS status, average grade) - the only card on this page
    /// that looks beyond the active programme, which is why it consumes the UNFILTERED history and
    /// goal list. With only the built-in programme, the card stays completely hidden (no
    /// "comparison" of a single entry).
    /// </summary>
    private static List<StatsProgramRowDto> BuildProgramComparison(StatsSummaryInput input)
    {
        var programs = input.StudyPrograms;
        if (programs.Count < 2) return new List<StatsProgramRowDto>();

        var rows = programs.Select(program =>
        {
            var catalog = input.ProgramCatalogs.FirstOrDefault(c => c.ProgramId == program.Id);
            var courses = catalog?.Courses ?? new List<CourseDto>();
            var courseIds = courses.Select(c => c.Id).ToHashSet();

            var studied = input.History
                .Where(s => courseIds.Contains(s.CourseId) && StudyMetrics.IsStudied(s, input.Now))
                .ToList();
            var hours = studied.Sum(s => (s.EndTime - s.StartTime).TotalHours);

            // Programme-aware ECTS calculation like for the active programme, just per programme.
            // Built-in programme: static quotas; otherwise the programme's own quotas, which are
            // empty when its detail row could not be loaded (groups then count as defensively
            // full, like AppStateService.GetActiveGroupQuotasAsync).
            IReadOnlyDictionary<string, int> quotas = program.Id is int
                ? catalog?.GroupQuotas ?? new Dictionary<string, int>()
                : CourseCatalog.GroupEctsQuotas;
            var ectsTotal = CourseCatalog.CalcTotalEcts(courses, quotas);
            var ectsEarned = CourseCatalog.CalcEctsEarned(courses, input.Settings.CompletedCourseIds, quotas);

            var avgGrade = StudyMetrics.CalcWeightedAverageGrade(input.Goals
                .Where(g => g.Grade.HasValue && courseIds.Contains(g.CourseId))
                .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, courses.First(c => c.Id == g.CourseId).Ects)));

            return new StatsProgramRowDto
            {
                Name = program.Name,
                IsActive = program.Id == input.Settings.ActiveStudyProgramId,
                IsCompleted = program.IsCompleted,
                Hours = hours,
                SessionCount = studied.Count,
                EctsEarned = ectsEarned,
                EctsTotal = ectsTotal,
                GradeLabel = avgGrade.HasValue ? StudyMetrics.FormatGrade(avgGrade.Value) : null,
                BarPercent = 0,
            };
        }).ToList();

        var maxHours = Math.Max(1.0, rows.Max(r => r.Hours));
        foreach (var row in rows)
            row.BarPercent = Math.Min(100, row.Hours / maxHours * 100);
        return rows;
    }
}
