using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
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
    private I18nText.ReportText T = new();
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

    protected override async Task OnInitializedAsync()
    {
        T = await I18nText.GetTextTableAsync<I18nText.ReportText>(this);
        _generatedAt = DateTime.Now;

        var settings = await State.GetSettingsAsync();
        var allCourses = await State.GetCoursesAsync();
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();

        var goalsUnfiltered = await State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals") ?? new();
        var goals = goalsUnfiltered.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();

        var history = (await State.GetJsonCachedAsync<List<StudySessionDto>>($"api/sessions/history?days={HistoryDays}") ?? new())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        var programs = await State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms") ?? new();
        _programmeName = programs.FirstOrDefault(p => p.Id == settings.ActiveStudyProgramId)?.Name
            ?? CourseCatalog.BuiltInProgramName;

        // Same composition of relevant course ids as Stats.razor.cs (selected +
        // completed + courses that actually have sessions).
        var relevantIds = settings.SelectedCourseIds
            .Concat(settings.CompletedCourseIds)
            .Concat(history.Select(s => s.CourseId))
            .Distinct();

        var raw = new List<(CourseDto Course, double Hours, int Count)>();
        foreach (var id in relevantIds)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == id);
            if (course == null) continue;
            var completedSessions = history.Where(s => s.CourseId == id && StudyMetrics.IsStudied(s, DateTime.Now)).ToList();
            if (completedSessions.Count == 0) continue;
            var hours = completedSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
            raw.Add((course, hours, completedSessions.Count));
        }

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
            .Select(g => (g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        _averageGradeLabel = averageGrade.HasValue
            ? averageGrade.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')
            : null;

        var groupQuotas = await State.GetActiveGroupQuotasAsync();
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

    private string StatusFor(StatsCourseListCard.CourseStatRow row)
    {
        if (row.IsCompleted) return T.StatusCompleted ?? "";
        if (row.DaysRemaining.HasValue)
        {
            return row.DaysRemaining.Value >= 0
                ? string.Format(T.StatusDueFormat ?? "", row.DaysRemaining.Value)
                : string.Format(T.StatusOverdueFormat ?? "", -row.DaysRemaining.Value);
        }
        return T.StatusInProgress ?? "";
    }
}
