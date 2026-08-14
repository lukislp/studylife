using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Calendar
{
    private DateTime _weekStart = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek + 1);
    private List<DateTime> _days = new();

    // View mode: "week" (default, desktop) or "day" (single column, mobile).
    // _currentDay is the day shown in day view; _weekStart/_days are always kept
    // up to date so switching back to week view works seamlessly.
    private string _viewMode = "week";
    private DateTime _currentDay = DateTime.Today;
    private bool _userChoseMode; // a manual toggle choice always wins over the auto-default
    private List<StudySession> _sessions = new();
    private List<CourseDto> _courses = new();
    private List<CourseGoalDto> _goals = new();
    private string _weekLabel = "";
    private I18nText.CalendarText T = new();

    // Scroll-to-"now": fire once on initial load, not on every subsequent
    // re-render (would constantly override the user's manual scrolling) - same
    // gate pattern as _heatmapScrolled in Stats.razor.cs. GoToday() resets the flag
    // so a deliberate jump to "today" re-centers again.
    private bool _scrolledToNow;

    protected override async Task OnInitializedAsync()
    {
        T = await I18nText.GetTextTableAsync<I18nText.CalendarText>(this);
        State.OnChange += OnStateChanged;
        var settings = await State.GetSettingsAsync();
        var allCourses = await State.GetCoursesAsync();
        _courses = allCourses
            .Where(c => settings.SelectedCourseIds.Contains(c.Id) && !settings.CompletedCourseIds.Contains(c.Id))
            .ToList();
        _sessions = await State.GetSessionsAsync();
        try
        {
            _goals = await State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals") ?? new();
        }
        catch
        {
            // Offline/flaky network: goals are just extra info (tags on the filter pills,
            // topic suggestion in the modal) — the calendar works fully without them.
            _goals = new();
        }
        await LoadTemplatesAsync();
        BuildWeek();

        // Fetch a .ics shared from the native app shell (always a no-op in the browser) -
        // AFTER loading courses, because the review flow preselects a default course.
        await ConsumeNativeIcsAsync();
    }

    private void OnStateChanged() => InvokeAsync(async () =>
    {
        var settings = await State.GetSettingsAsync();
        var allCourses = await State.GetCoursesAsync();
        _courses = allCourses
            .Where(c => settings.SelectedCourseIds.Contains(c.Id) && !settings.CompletedCourseIds.Contains(c.Id))
            .ToList();
        _sessions = await State.GetSessionsAsync();
        StateHasChanged();
    });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Attach once to the stable #cal-outer element (survives re-renders,
            // since Blazor reuses the static div during diffing).
            _swipeRef = DotNetObjectReference.Create(this);
            try
            {
                await JS.InvokeVoidAsync("initCalendarSwipe", "cal-outer", _swipeRef);
            }
            catch
            {
                // Best-effort: an installed PWA client may still serve a stale
                // index.html cached by the old service worker without initCalendarSwipe.
                // In that case the swipe gesture is simply missing (the arrow buttons and the
                // header swipe still work) — never show the error banner.
            }

            // Auto-default: switch to day view once on narrow viewports (phones).
            // Only on the first render and only as long as the user hasn't
            // operated the toggle themselves yet — the manual choice always wins.
            if (!_userChoseMode)
            {
                var narrow = false;
                try
                {
                    narrow = await JS.InvokeAsync<bool>("isNarrowViewport");
                }
                catch
                {
                    // Stale cached index.html without isNarrowViewport: degrade
                    // silently, week view remains the fallback.
                }
                if (narrow && !_userChoseMode && _viewMode != "day")
                {
                    _viewMode = "day";
                    _currentDay = DateTime.Today;
                    UpdateLabel();
                    StateHasChanged();
                }
            }
        }

        // Scrolls the hourly view so the "now" line (.cal-now-line) is visible,
        // instead of letting the user land at midnight (scroll-top 0). Only when
        // today actually lies within the visible range (week view: today in the
        // displayed week; day view: _currentDay == today) - otherwise there's no
        // .cal-now-line to scroll to at all (see CalendarDayColumn.razor: IsToday gate).
        // Placed outside "if (firstRender)" so GoToday() can reset the flag
        // and deliberately re-center, without firing on every other re-render.
        if (!_scrolledToNow && VisibleDays.Any(d => d.Date == DateTime.Today))
        {
            _scrolledToNow = true;
            try
            {
                await JS.InvokeVoidAsync("scrollElementToCurrentTime", "cal-outer");
            }
            catch
            {
                // Best-effort: same "stale cached index.html without this helper"
                // degradation as initCalendarSwipe above - never show the error banner,
                // the calendar just starts uncentered (default scroll position) instead.
            }
        }
    }

    public void Dispose()
    {
        State.OnChange -= OnStateChanged;
        // Fire-and-forget: removes the touch listeners if the element still exists
        // (after navigating away from the calendar it's usually already removed from the DOM,
        // in which case the call is a no-op and the listeners die with the element).
        try { _ = JS.InvokeVoidAsync("disposeCalendarSwipe", "cal-outer"); } catch { }
        _swipeRef?.Dispose();
        _swipeRef = null;
    }

    private void BuildWeek()
    {
        _days = Enumerable.Range(0, 7).Select(i => _weekStart.AddDays(i)).ToList();
        UpdateLabel();
    }

    private void UpdateLabel() => _weekLabel = _viewMode == "day"
        ? _currentDay.ToString("ddd, MMM d, yyyy")
        : $"{_weekStart:MMM d} – {_weekStart.AddDays(6):MMM d, yyyy}";

    // In day view, only _currentDay is rendered; week view shows _days.
    private IReadOnlyList<DateTime> VisibleDays => _viewMode == "day"
        ? new List<DateTime> { _currentDay }
        : _days;

    // Monday of the week containing d (same Monday anchoring as the recurring-series logic in SaveSession).
    private static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    // Mode-dependent navigation: day view ±1 day, week view ±7 days.
    private void Prev() { if (_viewMode == "day") MoveDay(-1); else { _weekStart = _weekStart.AddDays(-7); BuildWeek(); } }
    private void Next() { if (_viewMode == "day") MoveDay(1); else { _weekStart = _weekStart.AddDays(7); BuildWeek(); } }

    private void MoveDay(int delta)
    {
        _currentDay = _currentDay.AddDays(delta);
        // Carry _weekStart along when crossing a week boundary, so _days
        // (and thus week view when switching back) stays consistent.
        if (_currentDay < _weekStart || _currentDay >= _weekStart.AddDays(7))
        {
            _weekStart = MondayOf(_currentDay);
            BuildWeek();
        }
        else
        {
            UpdateLabel();
        }
    }

    private void GoToday()
    {
        _currentDay = DateTime.Today;
        _weekStart = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek + 1);
        BuildWeek();
        // Deliberate jump to "today": re-center on the "now" line again, even though
        // the initial scroll-to-now (first render) has already fired.
        _scrolledToNow = false;
    }

    private void SetViewMode(string mode)
    {
        if (_viewMode == mode) return;
        _userChoseMode = true;
        _viewMode = mode;
        if (mode == "day")
        {
            // Keep context: today if it lies within the displayed week,
            // otherwise the first day of the displayed week.
            _currentDay = DateTime.Today >= _weekStart && DateTime.Today < _weekStart.AddDays(7)
                ? DateTime.Today
                : _weekStart;
        }
        else if (_currentDay < _weekStart || _currentDay >= _weekStart.AddDays(7))
        {
            _weekStart = MondayOf(_currentDay);
            BuildWeek();
        }
        UpdateLabel();
    }

    // Swipe gesture on the header row for quick navigation without having to hit the
    // small arrow buttons. The header doesn't scroll horizontally, so a simple
    // Blazor touch handler without scroll-conflict handling is enough here.
    private double? _headerTouchStartX;
    private const double SwipeThreshold = 50;

    private void OnHeaderTouchStart(TouchEventArgs e)
        => _headerTouchStartX = e.Touches.Length > 0 ? e.Touches[0].ClientX : (double?)null;

    private void OnHeaderTouchEnd(TouchEventArgs e)
    {
        if (_headerTouchStartX == null) return;
        var startX = _headerTouchStartX.Value;
        _headerTouchStartX = null;
        if (e.ChangedTouches.Length == 0) return;

        var deltaX = e.ChangedTouches[0].ClientX - startX;
        if (deltaX > SwipeThreshold) Prev();
        else if (deltaX < -SwipeThreshold) Next();
    }

    // Swipe gesture on the calendar grid itself (#cal-outer): implemented entirely in JS
    // (initCalendarSwipe in index.html), because Blazor's TouchEventArgs don't provide the
    // scroll container's scrollLeft position needed for the edge condition.
    // JS calls the two [JSInvokable] methods below when it detects a gesture.
    private DotNetObjectReference<Calendar>? _swipeRef;

    // Method names must exactly match the strings in initCalendarSwipe (index.html).
    // Mode-dependent: day view ±1 day, week view ±7 days. In day view there's
    // no horizontal overflow, so the JS's scroll-edge conditions are trivially
    // satisfied and the gesture fires in both directions — exactly the desired behavior.
    [JSInvokable]
    public void SwipeNextWeek() { Next(); StateHasChanged(); }

    [JSInvokable]
    public void SwipePrevWeek() { Prev(); StateHasChanged(); }

}
