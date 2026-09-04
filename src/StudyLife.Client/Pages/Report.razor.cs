using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
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
    private bool _loaded;

    // Progressive render (2026-09 audit): renders the header/actions immediately, the document
    // body stays a skeleton (see Report.razor) until _loaded flips - the print doc is one
    // coherent unit, so unlike the dashboard/stats pages this stays a single phase.
    protected override bool RenderShellBeforeData => true;

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
        _historyTask = State.GetHistoryAsync(ReportSummaryBuilder.HistoryDays);
        _programsTask = State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms");
        _groupQuotasTask = State.GetActiveGroupQuotasAsync();
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        // The "generated on" timestamp is a page-level concern (when this document was printed),
        // not part of the report's DATA - read once, up front, independently of the builder's own
        // wall-clock input below (see ReportSummaryInput.Now's doc comment).
        _generatedAt = DateTime.Now;

        var settings = await _settingsTask!;
        var allCourses = await _coursesTask!;

        // Every number below comes from ReportSummaryBuilder (StudyLife.Shared), which the server
        // can run against the same inputs - this method only fetches, hands the raw inputs over,
        // and copies the result into the fields the markup binds to.
        var input = new ReportSummaryInput
        {
            Settings = AppStateService.ToDto(settings),
            AllCourses = allCourses,
            Goals = await _goalsUnfilteredTask! ?? new(),
            History = await _historyTask! ?? new(),
            StudyPrograms = await _programsTask! ?? new(),
            GroupQuotas = await _groupQuotasTask!,
            Now = DateTime.Now,
        };

        var summary = ReportSummaryBuilder.Build(input);

        _programmeName = summary.ProgrammeName;
        // StatsCourseListCard.CourseStatRow lives in the Client project, so the shared builder
        // hands back plain fields (ReportCourseRowDto) instead - mapped into the record here,
        // exactly as the original hand-written call did (BarPercent=0, the rest at their
        // stats-page-only defaults).
        _courseRows = summary.CourseRows
            .Select(r => new StatsCourseListCard.CourseStatRow(
                r.Course, r.Hours, r.SessionCount, r.IsCompleted, r.DaysRemaining, r.CompletionNote, r.Grade, 0))
            .ToList();
        _totalSessions = summary.TotalSessions;
        _totalHoursLabel = summary.TotalHoursLabel;
        _averageGradeLabel = summary.AverageGradeLabel;
        _ectsEarned = summary.EctsEarned;
        _ectsTotal = summary.EctsTotal;
        _ectsPercent = summary.EctsPercent;
        _periodStart = summary.PeriodStart;
        _periodEnd = summary.PeriodEnd;

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
