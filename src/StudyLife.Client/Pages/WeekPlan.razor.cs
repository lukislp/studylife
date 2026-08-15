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
    private I18nText.WeekPlanText T = new();
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

    protected override async Task OnInitializedAsync()
    {
        // Independent of each other - start them all immediately instead of await-ing one after
        // another (same pattern as Index.razor.cs/Setup.razor).
        var i18nTask = I18nText.GetTextTableAsync<I18nText.WeekPlanText>(this);
        var settingsTask = State.GetSettingsAsync();
        var coursesTask = State.GetCoursesAsync();
        var sessionsTask = State.GetSessionsAsync();

        T = await i18nTask;
        _generatedAt = DateTime.Now;
        // Monday start, same convention as Calendar.razor.cs (MondayOf) - here via the shared
        // StudyMetrics helper instead of its own copy of the offset calculation.
        _weekStart = StudyMetrics.WeekStartOf(DateTime.Today);
        _weekEnd = _weekStart.AddDays(6);

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
