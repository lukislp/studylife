// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Calendar
{
    private DateTime _weekStart;
    private List<DateTime> _days = new();

    // View mode: "week" (default, desktop) or "day" (single column, mobile).
    // _currentDay is the day shown in day view; _weekStart/_days are always kept
    // up to date so switching back to week view works seamlessly.
    private string _viewMode = "week";
    private DateTime _currentDay;
    private bool _userChoseMode; // a manual toggle choice always wins over the auto-default
    private List<StudySession> _sessions = new();
    private List<CourseDto> _courses = new();
    private List<CourseGoalDto> _goals = new();
    private string _weekLabel = "";

    // Initial scroll: armed on first load and on every navigation (view switch, paging, "today"),
    // consumed once the grid is rendered - not on every re-render (would constantly override the
    // user's manual scrolling), same gate pattern as _heatmapScrolled in Stats.razor.cs.
    private bool _scrollPending = true;

    // Progressive render (2026-09): gates the cal-outer grid until settings+courses+sessions
    // have loaded and BuildWeek() has run - everything the grid needs to render correctly
    // (course colors, session placement, week/day labels). Goals and templates (phase 2) only
    // feed the session dialog/templates modal, neither of which is open at load, so no separate
    // flag is needed for them - see OnTextLoadedAsync.
    protected override bool RenderShellBeforeData => true;
    private bool _gridLoading = true;

    // All fetches below are independent of each other, and of the text-table fetch that
    // LocalizedComponentBase starts in parallel - kicked off here (OnInitializingAsync, runs
    // alongside that fetch) instead of await-ing one after another, same pattern as
    // Index.razor.cs/Setup.razor. The templates fetch is duplicated inline (same shape as
    // LoadTemplatesAsync, used unchanged by its other call site) rather than started through that
    // method, since its own fetch can't be started this early without touching its signature.
    private Task<UserSettings>? _settingsTask;
    private Task<List<CourseDto>>? _coursesTask;
    private Task<List<StudySession>>? _sessionsTask;
    private Task<List<CourseGoalDto>?>? _goalsTask;
    private Task<List<SessionTemplateDto>?>? _templatesTask;

    protected override Task OnInitializingAsync()
    {
        _currentDay = Clock.LocalToday();
        _weekStart = MondayOf(_currentDay);
        _formRepeatUntil = _currentDay.AddDays(7);
        _settingsTask = State.GetSettingsAsync();
        _coursesTask = State.GetCoursesAsync();
        _sessionsTask = State.GetSessionsAsync();
        _goalsTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        _templatesTask = State.GetJsonCachedAsync<List<SessionTemplateDto>>("api/sessiontemplates");
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        State.OnChange += OnStateChanged;
        LocalDate.NamesChanged += OnDateNamesChanged;

        // ── Phase 1: settings + courses + sessions - the minimum the grid itself needs
        // (course colors/filter pills, session placement, BuildWeek's week/day label).
        var settings = await _settingsTask!;
        var allCourses = await _coursesTask!;
        _courses = allCourses
            .Where(c => settings.SelectedCourseIds.Contains(c.Id) && !settings.CompletedCourseIds.Contains(c.Id))
            .ToList();
        _sessions = await _sessionsTask!;
        BuildWeek();

        _gridLoading = false;
        await RenderPhaseAsync();

        // ── Phase 2: goals + templates. Both only feed the session dialog (topic suggestion,
        // "from template" dropdown) and the templates modal - neither is open yet at load time,
        // so nothing currently on screen needs to wait for this phase.
        try
        {
            _goals = await _goalsTask! ?? new();
        }
        catch
        {
            // Offline/flaky network: goals are just extra info (tags on the filter pills,
            // topic suggestion in the modal) — the calendar works fully without them.
            _goals = new();
        }
        try
        {
            _templates = await _templatesTask! ?? new();
        }
        catch
        {
            // Offline/flaky network: templates are a pure convenience feature, the calendar
            // works fully without them - same degradation as with _goals.
            _templates = new();
        }
        await RenderPhaseAsync();

        // Fetch a .ics shared from the native app shell (always a no-op in the browser) -
        // AFTER loading courses, because the review flow preselects a default course.
        await ConsumeNativeIcsAsync();
    }

    private void OnStateChanged() => InvokeAsync(async () =>
    {
        var sessionsTask = State.GetSessionsAsync();
        var settings = await State.GetSettingsAsync();
        var allCourses = await State.GetCoursesAsync();
        _courses = allCourses
            .Where(c => settings.SelectedCourseIds.Contains(c.Id) && !settings.CompletedCourseIds.Contains(c.Id))
            .ToList();
        _sessions = await sessionsTask;
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

            // Keyboard shortcuts and trackpad paging (Calendar.razor.js). Collocated module
            // instead of index.html; best-effort like the swipe above.
            try
            {
                _navModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Pages/Calendar.razor.js");
                await _navModule.InvokeVoidAsync("attach", _swipeRef, "cal-outer", CalendarNavigation.WheelThreshold);
            }
            catch
            {
                // Module unavailable (stale service-worker cache): the buttons and touch swipe still work.
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
                    _scrollPending = true;
                    _currentDay = Clock.LocalToday();
                    UpdateLabel();
                    StateHasChanged();
                }
            }
        }

        // Positions the hourly view (instead of landing at midnight): the current time when today
        // is visible, otherwise the first session of the visible days, otherwise 07:00 - see
        // CalendarInitialScroll. Only once the real grid is in the DOM (never on the progressive
        // render skeleton, where day view already reports a visible day but nothing can scroll),
        // and only when a navigation (first load, view switch, paging, "today") asked for it, so
        // ordinary re-renders never override the user's own scrolling.
        if (_scrollPending && !_gridLoading)
        {
            _scrollPending = false;
            var target = CalendarInitialScroll.Resolve(
                Clock.LocalNow(), VisibleDays, VisibleDays.SelectMany(SessionsForDay));
            try
            {
                await JS.InvokeVoidAsync("scrollElementToMinute", "cal-outer", target.Minute, target.ViewportFraction);
            }
            catch
            {
                // Best-effort: same "stale cached index.html without this helper"
                // degradation as initCalendarSwipe above - never show the error banner,
                // the calendar just starts at the default scroll position instead.
            }
        }

        // The grid scrolls inside #cal-outer (main-content is clipped on this page): hand it
        // the focus once it exists so arrows/space/page keys scroll it without a click first.
        // focusScrollRegion leaves a focus the user already put somewhere meaningful alone.
        if (!_gridLoading && !_gridFocused)
        {
            _gridFocused = true;
            try { await JS.InvokeVoidAsync("focusScrollRegion", "cal-outer"); }
            catch { /* stale cached index.html without the helper */ }
        }
    }

    private bool _gridFocused;

    // _weekLabel is built once per navigation, so a language switch (which is when the localized
    // weekday names arrive) leaves it stuck in the old language - same class of staleness as the
    // I18nLanguageWatcher cases, just for names that come from LocalDate instead of from T.
    private void OnDateNamesChanged() => InvokeAsync(() =>
    {
        UpdateLabel();
        StateHasChanged();
    });

    public void Dispose()
    {
        State.OnChange -= OnStateChanged;
        LocalDate.NamesChanged -= OnDateNamesChanged;
        // Fire-and-forget: removes the touch listeners if the element still exists
        // (after navigating away from the calendar it's usually already removed from the DOM,
        // in which case the call is a no-op and the listeners die with the element).
        try { _ = JS.InvokeVoidAsync("disposeCalendarSwipe", "cal-outer"); } catch { }
        try { _ = _navModule?.InvokeVoidAsync("detach"); } catch { }
        _swipeRef?.Dispose();
        _swipeRef = null;
    }

    private void BuildWeek()
    {
        _days = Enumerable.Range(0, 7).Select(i => _weekStart.AddDays(i)).ToList();
        UpdateLabel();
    }

    private void UpdateLabel() => _weekLabel = _viewMode == "day"
        ? $"{LocalDate.Weekday(_currentDay, true)}, {LocalDate.Short(_currentDay)}"
        : $"{LocalDate.DayMonth(_weekStart)} – {LocalDate.Short(_weekStart.AddDays(6))}";

    // In day view, only _currentDay is rendered; week view shows _days.
    private IReadOnlyList<DateTime> VisibleDays => _viewMode == "day"
        ? new List<DateTime> { _currentDay }
        : _days;

    // Monday of the week containing d (same Monday anchoring as the recurring-series logic in SaveSession).
    private static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    // Mode-dependent navigation: day view ±1 day, week view ±7 days.
    private void Prev() { _scrollPending = true; if (_viewMode == "day") MoveDay(-1); else { _weekStart = _weekStart.AddDays(-7); BuildWeek(); } }
    private void Next() { _scrollPending = true; if (_viewMode == "day") MoveDay(1); else { _weekStart = _weekStart.AddDays(7); BuildWeek(); } }

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
        _currentDay = Clock.LocalToday();
        _weekStart = MondayOf(Clock.LocalToday());
        BuildWeek();
        // Deliberate jump to "today": re-center on the "now" line again.
        _scrollPending = true;
    }

    private void SetViewMode(string mode)
    {
        if (_viewMode == mode) return;
        _userChoseMode = true;
        _scrollPending = true;
        _viewMode = mode;
        if (mode == "day")
        {
            // Keep context: today if it lies within the displayed week,
            // otherwise the first day of the displayed week.
            _currentDay = Clock.LocalToday() >= _weekStart && Clock.LocalToday() < _weekStart.AddDays(7)
                ? Clock.LocalToday()
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

    // Keyboard shortcuts and trackpad paging (Calendar.razor.js reports the DOM facts, the
    // decision is CalendarNavigation). Returns whether the key was consumed so the script can
    // suppress the browser default (ArrowLeft/Right would otherwise pan the grid).
    private IJSObjectReference? _navModule;

    [JSInvokable]
    public bool OnShortcutKey(string key, bool ctrl, bool meta, bool alt, bool shift, bool blocked)
        => ApplyNavigation(CalendarNavigation.FromKey(key, ctrl, meta, alt, shift, blocked));

    [JSInvokable]
    public void OnHorizontalGesture(double dx, double dy)
        => ApplyNavigation(CalendarNavigation.FromHorizontalGesture(dx, dy));

    private bool ApplyNavigation(CalendarNavStep step)
    {
        switch (step)
        {
            case CalendarNavStep.Previous: Prev(); break;
            case CalendarNavStep.Next: Next(); break;
            case CalendarNavStep.Today: GoToday(); break;
            default: return false;
        }
        StateHasChanged();
        return true;
    }

}
