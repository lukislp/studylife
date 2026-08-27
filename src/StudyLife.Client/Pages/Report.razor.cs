using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

/// <summary>
/// Printable study report (hours, ECTS, grades, course status) as a PDF substitute: no
/// PDF library, but a print-optimized view (print.css) + window.print() via the
/// printPage() interop already present in index.html - see docs/ARCHITECTURE.md. Deliberately
/// separate from the raw .db backup (DatabaseBackupService/BackupController): this report
/// is a human-readable record, not a restore file.
/// </summary>
public partial class Report
{
    /// <summary>Alias for StudyMetrics.CourseHoursResult (SessionCount renamed to Count) - kept
    /// local so the rest of this file doesn't need the fully qualified Shared type. The actual
    /// computation (previously an identical, independently hand-written copy of Stats.razor.cs's
    /// same loop) now lives in StudyMetrics.CalcCourseHours (metrics API, see MetricsController) -
    /// one implementation for both pages.</summary>
    private readonly record struct CourseHoursRow(CourseDto Course, double Hours, int Count);

    private bool _loaded;

    private DateTime _generatedAt;
    private DateTime? _periodStart;
    private DateTime? _periodEnd;
    private string? _programmeName;

    private List<StatsCourseListCard.CourseStatRow> _courseRows = new();
    private string _totalHoursLabel = "0h 0m";
    private int _totalSessions;
    private string? _averageGradeLabel;
    private int _ectsEarned;
    private int _ectsTotal;
    private double _ectsPercent;

    // Full history instead of the ±7/90-day window from AppStateService.GetSessionsAsync (which
    // is enough for dashboard/stats tiles, see Stats.razor.cs) - a study record must show the
    // ENTIRE study time to date. Same endpoint as Stats.razor (api/sessions/history),
    // just with a much larger days window (~10 years, practically "everything").
    private const int HistoryDays = 3650;

    // All fetches below are independent of each other, and of the text-table fetch that
    // LocalizedComponentBase starts in parallel - kicked off here (OnInitializingAsync, runs
    // alongside that fetch) instead of await-ing one after another (same pattern as
    // Index.razor.cs/Setup.razor/Stats.razor.cs).
    private Task<UserSettings>? _settingsTask;
    private Task<List<CourseDto>>? _coursesTask;
    private Task<List<CourseGoalDto>?>? _goalsUnfilteredTask;
    private Task<List<StudySessionDto>?>? _historyTask;
    private Task<List<StudyProgramSummaryDto>?>? _programsTask;
    private Task<IReadOnlyDictionary<string, int>>? _groupQuotasTask;

    protected override Task OnInitializingAsync()
    {
        _settingsTask = State.GetSettingsAsync();
        _coursesTask = State.GetCoursesAsync();
        _goalsUnfilteredTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        _historyTask = State.GetJsonCachedAsync<List<StudySessionDto>>($"api/sessions/history?days={HistoryDays}");
        _programsTask = State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms");
        _groupQuotasTask = State.GetActiveGroupQuotasAsync();
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        _generatedAt = DateTime.Now;

        var settings = await _settingsTask!;
        var allCourses = await _coursesTask!;
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();

        var goalsUnfiltered = await _goalsUnfilteredTask! ?? new();
        var goals = goalsUnfiltered.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();

        var history = (await _historyTask! ?? new())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        var programs = await _programsTask! ?? new();
        _programmeName = programs.FirstOrDefault(p => p.Id == settings.ActiveStudyProgramId)?.Name
            ?? CourseCatalog.BuiltInProgramName;

        // Same StudyMetrics.CalcCourseHours call as Stats.razor.cs (selected + completed +
        // courses that actually have sessions) - `history` is already StudySessionDto here
        // (unlike Stats.razor.cs's client-model `sessions`), so no mapping is needed.
        var raw = StudyMetrics.CalcCourseHours(
                allCourses, settings.SelectedCourseIds, settings.CompletedCourseIds, history, DateTime.Now)
            .Select(r => new CourseHoursRow(r.Course, r.Hours, r.SessionCount))
            .ToList();

        // Sorted by semester instead of by hours (as on the stats page) - for a
        // record, the chronological order of the study progression is the more natural reading.
        _courseRows = raw
            .OrderBy(r => r.Course.Semester).ThenByDescending(r => r.Hours)
            .Select(r =>
            {
                var goal = goals.FirstOrDefault(g => g.CourseId == r.Course.Id);
                int? daysRemaining = goal?.TargetDate.HasValue == true
                    ? (goal!.TargetDate!.Value.Date - DateTime.Today).Days
                    : null;
                var isCompleted = settings.CompletedCourseIds.Contains(r.Course.Id);
                return new StatsCourseListCard.CourseStatRow(
                    r.Course, r.Hours, r.Count, isCompleted, daysRemaining,
                    goal?.CompletionNote, goal?.Grade, 0);
            })
            .ToList();

        _totalSessions = _courseRows.Sum(r => r.SessionCount);
        var totalHours = _courseRows.Sum(r => r.Hours);
        _totalHoursLabel = $"{(int)totalHours}h {(int)((totalHours - (int)totalHours) * 60)}m";

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        _averageGradeLabel = averageGrade.HasValue
            ? StudyMetrics.FormatGrade(averageGrade.Value)
            : null;

        var groupQuotas = await _groupQuotasTask!;
        _ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, groupQuotas);
        _ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, groupQuotas);
        _ectsPercent = _ectsTotal > 0 ? Math.Min(100.0, _ectsEarned / (double)_ectsTotal * 100) : 0;

        if (history.Count > 0)
        {
            _periodStart = history.Min(s => s.StartTime.Date);
            _periodEnd = history.Max(s => s.StartTime.Date);
        }

        _loaded = true;
    }

    private async Task Print()
    {
        // printPage() already lives in index.html (best-effort window.print() wrapper) - no
        // new JS interop needed, see the task description/ARCHITECTURE.md on PDF export.
        try { await JS.InvokeVoidAsync("printPage"); } catch { /* best-effort, ignore */ }
    }

    // Only ever called from markup gated by both IsTextLoaded and _loaded (both set by this class's
    // own OnTextLoadedAsync before any row is rendered) - T is guaranteed loaded, no defensive
    // "?? """ needed here.
    private string StatusFor(StatsCourseListCard.CourseStatRow row)
    {
        if (row.IsCompleted) return T.StatusCompleted;
        if (row.DaysRemaining.HasValue)
        {
            return row.DaysRemaining.Value >= 0
                ? string.Format(T.StatusDueFormat, row.DaysRemaining.Value)
                : string.Format(T.StatusOverdueFormat, -row.DaysRemaining.Value);
        }
        return T.StatusInProgress;
    }
}
