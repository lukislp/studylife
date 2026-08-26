using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    /// <summary>Per-course aggregate hours/session-count for the current filter set. Record
    /// struct instead of a value tuple - LINQ over a List<(...)> of value tuples has triggered a
    /// Mono AOT crash at compile time (not call time) in the native app shell (studylife-app,
    /// BlazorWebView) that links this same Client project - see
    /// project_studylife_app_ios_aot_linq_tuple_crash.</summary>
    internal readonly record struct CourseHoursRow(CourseDto Course, double Hours, int Count);

    private List<StatsCourseListCard.CourseStatRow> _courseRows = new();
    private string _totalHoursLabel = "0h";
    private int _totalSessions;
    private string _averageGradeLabel = "–";
    private int _ectsEarned;
    private int _ectsTotal;
    private double _ectsPercent;
    private I18nLanguageWatcher _langWatcher = null!;
    // Active-programme course list from OnTextLoadedAsync, kept around so the OnAfterRenderAsync
    // language-switch relocalization below can re-resolve course names (incl. T.CourseFallback for
    // since-deleted courses) without re-fetching or re-running the expensive Build* pipeline.
    private List<CourseDto> _allCourses = new();

    private const int HistoryDays = 371;

    private bool _heatmapScrolled;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Not gated on firstRender: OnTextLoadedAsync's data load means the component
        // renders once before _heatmapWeeks is populated (established Blazor lifecycle
        // gotcha in this codebase) - wait until the heatmap actually has data to scroll.
        if (!_heatmapScrolled && _heatmapWeeks.Count > 0)
        {
            _heatmapScrolled = true;
            // Best-effort: a stale cached index.html (installed PWA, old service worker)
            // may not know this helper yet - then the heatmap simply starts unscrolled.
            try { await JS.InvokeVoidAsync("scrollElementToRight", "heatmap-scroll"); } catch { /* ignore */ }
        }

        // Toolbelt.Blazor.I18nText auto-updates T's own fields and re-renders this component when
        // the active language changes, but text baked from T into stored chart data at
        // OnTextLoadedAsync time doesn't recompute on its own - same root cause/fix shape as
        // Planner.razor/Index.razor.cs. Gated on !firstRender (unlike the heatmap-scroll block
        // above, this has nothing to do with waiting for data to arrive). _langWatcher can still be
        // null on an early render pass - same established gotcha as the heatmap-scroll comment
        // above, just for OnTextLoadedAsync's own not-yet-finished state this time.
        if (!firstRender && _langWatcher != null && await _langWatcher.CheckChangedAsync())
        {
            RefreshWeekdayHours();
            RefreshTimeHeatmapRows();
            RefreshMonthlyBreakdown();
            RefreshHeatmapCourseNames();
            RefreshDonutCourseNames();
            RefreshSemesterComparisonLabels();
            RefreshGradePointLabels();
            RefreshCourseComparisonLabels();
            await InvokeAsync(StateHasChanged);
        }
    }

    // All fetches below are independent of each other, and of the text-table fetch that
    // LocalizedComponentBase starts in parallel - kicked off here (OnInitializingAsync, runs
    // alongside that fetch) instead of await-ing one after another once text has loaded, same
    // pattern as Index.razor.cs/Setup.razor. Safe to start GetCoursesAsync/
    // GetActiveGroupQuotasAsync alongside GetSettingsAsync: their internal settings lookup shares
    // the same de-duplicated in-flight task (AppStateService.GetSettingsAsync) instead of firing
    // a second request.
    private Task<UserSettings>? _settingsTask;
    private Task<List<CourseGoalDto>?>? _goalsUnfilteredTask;
    private Task<List<CourseDto>>? _coursesTask;
    private Task<List<StudySession>>? _sessionsTask;
    private Task<List<NoteDto>?>? _notesTask;
    private Task<List<StudySessionDto>?>? _historyAllTask;
    private Task<IReadOnlyDictionary<string, int>>? _groupQuotasTask;
    private Task<List<StudySessionDto>?>? _historyAllTimeTask;
    private Task<IReadOnlyList<(DateTime Date, double Vo2Max)>?>? _cardioFitnessTask;

    protected override Task OnInitializingAsync()
    {
        _settingsTask = State.GetSettingsAsync();
        _goalsUnfilteredTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        _coursesTask = State.GetCoursesAsync();
        _sessionsTask = State.GetSessionsAsync();
        _notesTask = State.GetJsonCachedAsync<List<NoteDto>>("api/notes");
        _historyAllTask = State.GetJsonCachedAsync<List<StudySessionDto>>($"api/sessions/history?days={HistoryDays}");
        _groupQuotasTask = State.GetActiveGroupQuotasAsync();
        _historyAllTimeTask = State.GetJsonCachedAsync<List<StudySessionDto>>("api/sessions/history?days=3650");
        _cardioFitnessTask = Health.IsAvailable
            ? Health.GetCardioFitnessHistoryAsync(365)
            : Task.FromResult<IReadOnlyList<(DateTime Date, double Vo2Max)>?>(null);
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        _langWatcher = new I18nLanguageWatcher(I18nText);
        await _langWatcher.InitAsync();
        var settings = await _settingsTask!;
        var goalsUnfiltered = await _goalsUnfilteredTask! ?? new();
        var allCourses = await _coursesTask!;
        _allCourses = allCourses;
        // Active-programme scope: allCourses is already limited to the active programme
        // (AppStateService.GetCoursesAsync). `sessions` (near-term, course rows above), `history`
        // (long-term, all charts below), `goals` (grades/deadlines) and course-bound `notes` get
        // filtered ONCE here by the active programme's course ids - so switching programmes
        // really only shows its data everywhere on the page, not other
        // programmes'. General (course-less) notes always stay visible.
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();
        var goals = goalsUnfiltered.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();
        var sessions = (await _sessionsTask!).Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        // For the notes/study-time correlation card - its own fetch because otherwise no
        // notes data would be needed on this page (see Stats.Comparisons.razor.cs).
        var notes = (await _notesTask! ?? new())
            .Where(n => !n.CourseId.HasValue || activeCourseIds.Contains(n.CourseId.Value))
            .ToList();
        // Shared long-term history (12 months) for the heatmap, donut, weekday/time-of-day, and
        // monthly-trend charts as well as the per-course trend arrows below. Deliberately separate
        // from `sessions` above (AppStateService, ±7/90-day window) - see /api/sessions/history.
        // historyAll stays unfiltered for the programme comparison (Stats.Programs.razor.cs),
        // the only card that looks beyond the active programme.
        var historyAll = await _historyAllTask! ?? new();
        var history = historyAll
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        var relevantIds = settings.SelectedCourseIds
            .Concat(settings.CompletedCourseIds)
            .Concat(sessions.Select(s => s.CourseId))
            .Distinct();

        var raw = new List<CourseHoursRow>();
        foreach (var id in relevantIds)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == id);
            if (course == null) continue;
            // "Studied" = timer-completed OR the scheduled time has simply passed (not every
            // session runs through the in-app timer, e.g. reading offline at the lake).
            var completedSessions = sessions.Where(s => s.CourseId == id && (s.IsCompleted || s.EndTime <= DateTime.Now)).ToList();
            if (completedSessions.Count == 0) continue;
            var hours = completedSessions.Sum(s => s.Duration.TotalHours);
            raw.Add(new CourseHoursRow(course, hours, completedSessions.Count));
        }

        var maxHours = raw.Count == 0 ? 1 : Math.Max(1, raw.Max(r => r.Hours));
        var trends = BuildCourseTrends(history);
        var sparks = BuildCourseSparks(history);

        _courseRows = raw
            .OrderByDescending(r => r.Hours)
            .Select(r =>
            {
                var goal = goals.FirstOrDefault(g => g.CourseId == r.Course.Id);
                int? daysRemaining = goal?.TargetDate.HasValue == true
                    ? (goal!.TargetDate!.Value.Date - DateTime.Today).Days
                    : null;
                var isCompleted = settings.CompletedCourseIds.Contains(r.Course.Id);
                return new StatsCourseListCard.CourseStatRow(
                    r.Course, r.Hours, r.Count, isCompleted, daysRemaining,
                    goal?.CompletionNote, goal?.Grade, Math.Min(100, r.Hours / maxHours * 100),
                    trends.GetValueOrDefault(r.Course.Id),
                    sparks.GetValueOrDefault(r.Course.Id),
                    CalcEctsRingPercent(r.Course, isCompleted, goal),
                    isCompleted ? r.Course.Ects : 0);
            })
            .ToList();

        _totalSessions = _courseRows.Sum(r => r.SessionCount);
        var totalHours = _courseRows.Sum(r => r.Hours);
        _totalHoursLabel = $"{(int)totalHours}h {(int)((totalHours - (int)totalHours) * 60)}m";

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => (g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        if (averageGrade.HasValue)
            _averageGradeLabel = StudyMetrics.FormatGrade(averageGrade.Value);

        BuildGradeHistory(goals, allCourses);
        BuildGradeTimeline(goals, allCourses);
        BuildHoursGradeScatter(goals, allCourses, raw);
        BuildHoursEctsScatter(allCourses, raw, settings);
        BuildGradeDistribution(goals);
        BuildCardioFitnessTrend(await _cardioFitnessTask!);

        // Programme-aware: quotas of the ACTIVE programme (built-in: static, otherwise via fetch).
        var groupQuotas = await _groupQuotasTask!;
        _ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, groupQuotas);
        _ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, groupQuotas);
        _ectsPercent = _ectsTotal > 0 ? Math.Min(100.0, _ectsEarned / (double)_ectsTotal * 100) : 0;

        BuildForecast(settings, allCourses, history);
        BuildHeatmap(history, allCourses);
        BuildDonut(history, allCourses);
        BuildWeekdayAndTimeOfDay(history);
        BuildTimeHeatmap(history, allCourses);
        BuildMonthlyStacks(history, allCourses);
        BuildMonthComparison(history);
        BuildEctsTimeline(goals, allCourses);
        BuildEctsPlan(goals, allCourses, settings);
        BuildProductivityScore(history);
        BuildGoalHistory(history, settings);
        BuildInactivityTrend(history);
        BuildSessionLengthHistogram(history);
        BuildCourseComparison(history, allCourses);
        BuildNotesCorrelation(history, notes);
        BuildCourseBalance(history, allCourses, settings);

        // Separate all-time fetch ONLY for the semester comparison: its average-hours figure for
        // earlier semesters needs sessions beyond the 371-day window above - the same
        // 10-year convention as Index.Achievements' AchievementHistoryDays.
        var historyAllTime = (await _historyAllTimeTask! ?? new())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();
        BuildSemesterComparison(historyAllTime, goals, allCourses, settings);

        await BuildProgramComparisonAsync(historyAll, goalsUnfiltered, settings);
    }

    /// <summary>
    /// Weekly hours for the last 12 weeks per course (same Monday grid as LastNWeekStarts),
    /// normalized to the course's own maximum - the data basis for the mini sparklines in the
    /// course list. Courses without sessions in the window are absent from the dictionary (no empty sparkline).
    /// </summary>
    private static Dictionary<int, List<double>> BuildCourseSparks(List<StudySessionDto> history)
    {
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount);
        var windowStart = weekStarts[0];
        var sparks = new Dictionary<int, List<double>>();
        foreach (var group in history
            .Where(s => StudyMetrics.IsStudied(s, DateTime.Now) && s.StartTime.Date >= windowStart)
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

    // Course ECTS are all-or-nothing - for ongoing courses, the topic progress
    // (setup checklist, same CompletedTopics semantics as the dashboard) fills the ring as an
    // interim state, so it isn't just binary empty/full.
    private static double CalcEctsRingPercent(CourseDto course, bool isCompleted, CourseGoalDto? goal)
    {
        if (isCompleted) return 100;
        if (course.Topics.Count == 0) return 0;
        var completedTopics = string.IsNullOrWhiteSpace(goal?.CompletedTopics)
            ? new HashSet<string>()
            : goal.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return course.Topics.Count(t => completedTopics.Contains(t)) / (double)course.Topics.Count * 100;
    }
}
