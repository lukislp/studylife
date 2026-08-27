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
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        _generatedAt = DateTime.Now;
        // Monday start, same convention as Calendar.razor.cs (MondayOf) - here via the shared
        // StudyMetrics helper instead of its own copy of the offset calculation.
        _weekStart = StudyMetrics.WeekStartOf(DateTime.Today);
        _weekEnd = _weekStart.AddDays(6);

        var settings = await _settingsTask!;
        var allCourses = await _coursesTask!;
        _courseLookup = allCourses.ToDictionary(c => c.Id);

        var sessions = await _sessionsTask!;
        var weekSessions = sessions
            .Where(s => s.StartTime.Date >= _weekStart && s.StartTime.Date <= _weekEnd)
            .OrderBy(s => s.StartTime)
            .ToList();

        _totalSessions = weekSessions.Count;
        var totalHours = weekSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _plannedHoursLabel = $"{(int)totalHours}h {(int)((totalHours - (int)totalHours) * 60)}m";

        _weeklyGoalMin = settings.WeeklyGoalMinHours;
        _weeklyGoalMax = settings.WeeklyGoalMaxHours;

        _dayGroups = Enumerable.Range(0, 7)
            .Select(i => _weekStart.AddDays(i))
            .Select(day => new DayGroup(
                day.ToString("dddd, dd.MM."),
                weekSessions.Where(s => s.StartTime.Date == day).OrderBy(s => s.StartTime).ToList()))
            .ToList();

        _loaded = true;
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
