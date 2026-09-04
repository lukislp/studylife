using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

/// <summary>
/// Printable weekly overview of the PLANNED sessions for this calendar week - the forward-looking
/// counterpart to Report.razor (there a retrospective study record, here "what's coming up this
/// week"). Reuses the same print.css/window.print() interop as there (printPage() from
/// index.html, .report-page/.report-section/.report-table classes from stats.css/print.css),
/// see docs/ARCHITECTURE.md. Data source: AppStateService.GetSessionsAsync() (±7/90-day window
/// from SessionsController.GetAll) instead of a new endpoint - always covers the current week.
/// </summary>
public partial class WeekPlan
{
    private bool _loaded;

    // Progressive render (2026-09 audit): renders the header/actions immediately, the document
    // body stays a skeleton (see WeekPlan.razor) until _loaded flips - the print doc is one
    // coherent unit, so unlike the dashboard/stats pages this stays a single phase.
    protected override bool RenderShellBeforeData => true;

    private DateTime _generatedAt;
    private DateTime _weekStart;
    private DateTime _weekEnd;
    private int _totalSessions;
    private string _plannedHoursLabel = "0h 0m";
    private int _weeklyGoalMin;
    private int _weeklyGoalMax;
    private List<DayGroup> _dayGroups = new();
    private Dictionary<int, CourseDto> _courseLookup = new();

    // Independent of each other, and of the text-table fetch that LocalizedComponentBase starts
    // in parallel - kicked off here (OnInitializingAsync, runs alongside that fetch) instead of
    // await-ing one after another (same pattern as Index.razor.cs/Setup.razor).
    private Task<UserSettings>? _settingsTask;
    private Task<List<CourseDto>>? _coursesTask;
    private Task<List<StudySession>>? _sessionsTask;

    protected override Task OnInitializingAsync()
    {
        _settingsTask = State.GetSettingsAsync();
        _coursesTask = State.GetCoursesAsync();
        _sessionsTask = State.GetSessionsAsync();

        // Live updates (change stream): this page only ever reads sessions/settings, so those two
        // events are the whole story - no OnServerChanged subscription needed here.
        State.OnSessionsChanged += OnLiveDataChanged;
        State.OnSettingsChanged += OnLiveDataChanged;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        State.OnSessionsChanged -= OnLiveDataChanged;
        State.OnSettingsChanged -= OnLiveDataChanged;
    }

    private void OnLiveDataChanged() => InvokeAsync(RefreshAsync);

    // Coalesced, non-overlapping live refresh - same shape as Stats.razor.cs: re-runs the load
    // with fresh data but never resets _loaded back to false, so the printed document stays on
    // screen until the fresh numbers replace it. An event arriving mid-refresh just schedules one
    // more pass afterwards instead of overlapping it; a failed refresh is swallowed so the next
    // event simply tries again.
    private bool _refreshRunning;
    private bool _refreshPending;

    private async Task RefreshAsync()
    {
        if (_refreshRunning) { _refreshPending = true; return; }
        _refreshRunning = true;
        try
        {
            do
            {
                _refreshPending = false;
                try { await LoadDataAsync(isRefresh: true); }
                catch { /* background refresh failed - keep showing the last good state, next event retries */ }
            } while (_refreshPending);
        }
        finally { _refreshRunning = false; }
    }

    protected override async Task OnTextLoadedAsync() => await LoadDataAsync();

    private async Task LoadDataAsync(bool isRefresh = false)
    {
        // "Generated on"/the current week are page-level, wall-clock concerns - refreshed on
        // every live update too, so the printed stamp and week window always match the moment
        // the data was actually rebuilt.
        _generatedAt = DateTime.Now;
        // Monday start, same convention as Calendar.razor.cs (MondayOf) - here via the shared
        // StudyMetrics helper instead of its own copy of the offset calculation.
        _weekStart = StudyMetrics.WeekStartOf(DateTime.Today);
        _weekEnd = _weekStart.AddDays(6);

        // A live refresh can't reuse OnInitializingAsync's tasks (those already resolved to the
        // page's initial data) - it starts its own, exactly like the initial load did.
        var settingsTask = isRefresh ? State.GetSettingsAsync() : _settingsTask!;
        var coursesTask = isRefresh ? State.GetCoursesAsync() : _coursesTask!;
        var sessionsTask = isRefresh ? State.GetSessionsAsync() : _sessionsTask!;

        var settings = await settingsTask;
        var allCourses = await coursesTask;
        _courseLookup = allCourses.ToDictionary(c => c.Id);

        var sessions = await sessionsTask;
        var weekSessions = sessions
            .Where(s => s.StartTime.Date >= _weekStart && s.StartTime.Date <= _weekEnd)
            .OrderBy(s => s.StartTime)
            .ToList();

        _totalSessions = weekSessions.Count;
        var totalHours = weekSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _plannedHoursLabel = StudyMetrics.FormatHoursMinutes(totalHours);

        _weeklyGoalMin = settings.WeeklyGoalMinHours;
        _weeklyGoalMax = settings.WeeklyGoalMaxHours;

        _dayGroups = Enumerable.Range(0, 7)
            .Select(i => _weekStart.AddDays(i))
            .Select(day => new DayGroup(
                day.ToString("dddd, dd.MM."),
                weekSessions.Where(s => s.StartTime.Date == day).OrderBy(s => s.StartTime).ToList()))
            .ToList();

        _loaded = true;
        await RenderPhaseAsync();
    }

    private async Task Print()
    {
        // printPage() already lives in index.html (best-effort window.print() wrapper) - no
        // new JS interop needed, same pattern as Report.razor.cs.
        try { await JS.InvokeVoidAsync("printPage"); } catch { /* best-effort, ignore */ }
    }

    private string IconFor(int courseId) => _courseLookup.TryGetValue(courseId, out var c) ? c.Icon : "📚";

    private record DayGroup(string DayLabel, List<StudySession> Sessions);
}
