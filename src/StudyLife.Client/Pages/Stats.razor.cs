using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsCourseListCard.CourseStatRow> _courseRows = new();
    private string _totalHoursLabel = "0h";
    private int _totalSessions;
    private string _averageGradeLabel = "–";
    private int _ectsEarned;
    private int _ectsTotal;
    private double _ectsPercent;
    private I18nLanguageWatcher _langWatcher = null!;
    // Active-programme course list from LoadDataAsync, kept around so the OnAfterRenderAsync
    // language-switch relocalization below can re-resolve course names (incl. T.CourseFallback for
    // since-deleted courses) without re-fetching or re-running the expensive builder pipeline.
    private List<CourseDto> _allCourses = new();

    private bool _heatmapScrolled;

    // Progressive render (2026-09 audit): default true so the first LoadDataAsync run shows a
    // skeleton for the sections each flag covers instead of their empty/zero field defaults.
    // _statsLoading covers the large majority of the page (settings+courses+goals+sessions+the
    // 12-month history all of those cards share); _notesLoading/_extendedLoading gate the few
    // cards whose own fetch is deliberately deferred behind slower or less essential data (notes,
    // native-health cardio fitness, the 10-year semester comparison, cross-programme comparison).
    private bool _statsLoading = true;
    private bool _notesLoading = true;
    private bool _extendedLoading = true;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Not gated on firstRender: LoadDataAsync's data load means the component
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
        // LoadDataAsync time doesn't recompute on its own - same root cause/fix shape as
        // Planner.razor/Index.razor.cs. Gated on !firstRender (unlike the heatmap-scroll block
        // above, this has nothing to do with waiting for data to arrive). _langWatcher can still be
        // null on an early render pass - same established gotcha as the heatmap-scroll comment
        // above, just for LoadDataAsync's own not-yet-finished state this time.
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
    private Task<IReadOnlyList<StudyLife.Client.Services.CardioFitnessPoint>?>? _cardioFitnessTask;

    protected override bool RenderShellBeforeData => true;

    protected override Task OnInitializingAsync()
    {
        _settingsTask = State.GetSettingsAsync();
        _coursesTask = State.GetCoursesAsync();
        _cardioFitnessTask = Health.IsAvailable
            ? Health.GetCardioFitnessPointsAsync(365)
            : Task.FromResult<IReadOnlyList<StudyLife.Client.Services.CardioFitnessPoint>?>(null);
        // One wall-clock read for the whole page: sent to the server and used by the local
        // fallback alike, so both paths compute every time-dependent number for the same instant.
        _now = DateTime.Now;
        // Server path: one response with the complete builder output replaces the seven raw
        // fetches (sessions, two history windows, goals, quotas, notes, programmes + their N+1
        // catalogs). The raw fetches only start in the fallback, see StartRawFetches.
        _summaryTask = State.TryGetSummaryAsync<StatsSummaryDto>("api/stats/summary", _now);

        // Live updates (change stream): sessions/settings have their own events, notes/course
        // goals/study programmes fall under the generic "something else changed" kind.
        State.OnSessionsChanged += OnLiveDataChanged;
        State.OnSettingsChanged += OnLiveDataChanged;
        State.OnServerChanged += OnServerChanged;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        State.OnSessionsChanged -= OnLiveDataChanged;
        State.OnSettingsChanged -= OnLiveDataChanged;
        State.OnServerChanged -= OnServerChanged;
    }

    private void OnLiveDataChanged() => InvokeAsync(RefreshAsync);

    private void OnServerChanged(string? kind)
    {
        if (kind is not (null or "notes" or "coursegoals" or "studyprograms")) return;
        _ = InvokeAsync(RefreshAsync);
    }

    // Coalesced, non-overlapping live refresh: a change-stream event re-runs LoadDataAsync with
    // fresh data, but (unlike the initial load) never resets _statsLoading/_notesLoading/
    // _extendedLoading back to true - the numbers already on screen stay visible until each
    // phase's fresh result replaces them, so a background sync never flashes the skeletons back
    // in. An event that arrives while a refresh is already running just schedules one more pass
    // afterwards instead of overlapping it; a failed refresh is swallowed so the page keeps
    // showing its last good state and the next event simply tries again.
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

    private DateTime _now;
    private Task<StatsSummaryDto?>? _summaryTask;

    /// <summary>Fallback only (offline or a server without api/stats/summary): the raw fetches the
    /// local builder needs, started together so the fallback still overlaps its round trips.</summary>
    private void StartRawFetches()
    {
        _goalsUnfilteredTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        _sessionsTask = State.GetSessionsAsync();
        _notesTask = State.GetJsonCachedAsync<List<NoteDto>>("api/notes");
        _historyAllTask = State.GetHistoryAsync(StatsSummaryBuilder.HistoryDays);
        _groupQuotasTask = State.GetActiveGroupQuotasAsync();
        _historyAllTimeTask = State.GetHistoryAsync(StatsSummaryBuilder.AllTimeHistoryDays);
    }

    protected override async Task OnTextLoadedAsync()
    {
        _langWatcher = new I18nLanguageWatcher(I18nText);
        await _langWatcher.InitAsync();
        await LoadDataAsync();
    }

    /// <summary>
    /// Fetches, hands the raw inputs to <see cref="StatsSummaryBuilder"/> and copies each phase's
    /// result into the fields the markup binds to. Every number on this page comes from that
    /// shared builder (StudyLife.Shared), which the server can run against the same inputs - this
    /// method only fetches and localizes. The builder is called once per phase (rather than once
    /// for everything) precisely so the page's three progressive-render boundaries survive: a
    /// single call would have to wait for the ~10-year history before showing anything at all.
    /// </summary>
    private async Task LoadDataAsync(bool isRefresh = false)
    {
        // One wall-clock read for the whole build: nothing in the builder reads DateTime.Now/Today
        // itself, so every card below sees the exact same instant. A live refresh takes a fresh
        // instant instead of reusing the one from the initial page load.
        var now = isRefresh ? DateTime.Now : _now;
        _now = now;

        // A live refresh can't reuse OnInitializingAsync's tasks (those already resolved to the
        // page's initial data) - it starts its own fresh ones, exactly like the initial load did.
        var settingsTask = isRefresh ? State.GetSettingsAsync() : _settingsTask!;
        var coursesTask = isRefresh ? State.GetCoursesAsync() : _coursesTask!;
        var cardioFitnessTask = isRefresh
            ? (Health.IsAvailable ? Health.GetCardioFitnessPointsAsync(365) : Task.FromResult<IReadOnlyList<StudyLife.Client.Services.CardioFitnessPoint>?>(null))
            : _cardioFitnessTask!;
        var summaryTask = isRefresh
            ? State.TryGetSummaryAsync<StatsSummaryDto>("api/stats/summary", now)
            : _summaryTask!;

        // ── Phase 1: settings + courses + goals + sessions + the 12-month history - the data
        // basis for the course list/totals and the large majority of charts on this page (2026-09
        // progressive-render audit). Grouping these awaits together instead of interleaving them
        // with the notes/cardio-fitness/semester-comparison/programme-comparison fetches further
        // down lets the bulk of the page render as soon as this batch resolves, instead of
        // waiting on the slower/less essential fetches too - the tasks themselves already started
        // above (or in OnInitializingAsync) regardless of await order, so this only changes WHEN
        // each result is consumed/rendered, never what is fetched.
        var settings = await settingsTask;
        var allCourses = await coursesTask;
        _allCourses = allCourses;

        var summary = await summaryTask;
        if (summary != null)
        {
            // Same three phases and flags as the fallback below, only the data arrives in one piece.
            ApplyCore(summary.Core);
            _statsLoading = false;
            await RenderPhaseAsync();

            ApplyNotes(summary.Notes);
            _notesLoading = false;
            await RenderPhaseAsync();

            BuildCardioFitnessTrend(await cardioFitnessTask);
            ApplyExtended(summary.Extended);
            _extendedLoading = false;
            await RenderPhaseAsync();
            return;
        }

        StartRawFetches();

        // The builder's input is filled in phase by phase, in the same order the page awaits its
        // fetches - each phase only reads the fields its own group needs (see the phase methods).
        // Every list stays UNSCOPED here: the builder applies the active-programme filter itself,
        // so the cross-programme comparison still sees every programme's data while every other
        // card is scoped, exactly as before.
        var input = new StatsSummaryInput
        {
            Settings = AppStateService.ToDto(settings),
            AllCourses = allCourses,
            Now = now,
        };
        input.Goals = await _goalsUnfilteredTask! ?? new();
        input.Sessions = (await _sessionsTask!).Select(AppStateService.ToDto).ToList();
        // Shared long-term history (12 months) for the heatmap, donut, weekday/time-of-day, and
        // monthly-trend charts as well as the per-course trend arrows. Deliberately separate from
        // the session list above (AppStateService, ±7/90-day window) - see /api/sessions/history.
        input.History = await _historyAllTask! ?? new();
        // Phase 1 too, not only phase 3: the course rows/totals are built from the FULL history
        // (StatsSummaryBuilder.BuildCore), so that a course last studied before the 12-month
        // window still gets a row. Both fetches started together in StartRawFetches, so this only
        // moves where the result is consumed.
        input.HeavyHistory = await _historyAllTimeTask! ?? new();
        // Programme-aware: quotas of the ACTIVE programme (built-in: static, otherwise via fetch).
        input.GroupQuotas = await _groupQuotasTask!;

        ApplyCore(StatsSummaryBuilder.BuildCore(input));

        _statsLoading = false;
        await RenderPhaseAsync();

        // ── Phase 2: notes-dependent correlation card. Its own fetch because otherwise no notes
        // data would be needed on this page (see Stats.Comparisons.razor.cs) - deferred behind the
        // main phase above so a slow notes fetch never holds back the rest of the page.
        input.Notes = await _notesTask! ?? new();

        ApplyNotes(StatsSummaryBuilder.BuildNotes(input));

        _notesLoading = false;
        await RenderPhaseAsync();

        // ── Phase 3: extended/slower fetches - native-app-only cardio fitness (can be genuinely
        // slow on-device), the 10-year semester comparison, and the cross-programme comparison
        // (its own N+1 fetch across every programme). The cardio-fitness card is deliberately NOT
        // part of the shared builder: native health data never leaves the device.
        BuildCardioFitnessTrend(await cardioFitnessTask);

        // input.HeavyHistory is already filled (phase 1) - the semester comparison here needs the
        // same all-time list, since its average-hours figure for earlier semesters reaches beyond
        // the 12-month window.
        (input.StudyPrograms, input.ProgramCatalogs) = await LoadProgramCatalogsAsync();

        ApplyExtended(StatsSummaryBuilder.BuildExtended(input));

        _extendedLoading = false;
        await RenderPhaseAsync();
    }

    /// <summary>Phase 1 result -> the fields the markup binds to. Only the localized strings are
    /// assembled here (in the partials below); every number/label already comes from the
    /// builder.</summary>
    private void ApplyCore(StatsCoreSummaryDto core)
    {
        _courseRows = core.CourseRows
            .Select(r => new StatsCourseListCard.CourseStatRow(
                r.Course, r.Hours, r.SessionCount, r.IsCompleted, r.DaysRemaining,
                r.CompletionNote, r.Grade, r.BarPercent, r.TrendPercent, r.Spark,
                r.RingPercent, r.EctsEarned))
            .ToList();

        _totalSessions = core.TotalSessions;
        _totalHoursLabel = core.TotalHoursLabel;
        _averageGradeLabel = core.AverageGradeLabel;

        _ectsEarned = core.EctsEarned;
        _ectsTotal = core.EctsTotal;
        _ectsPercent = core.EctsPercent;

        ApplyGrades(core);
        ApplyCharts(core);
        ApplyTrends(core);
        ApplyComparisons(core);
    }

    /// <summary>Phase 3 result -> fields.</summary>
    private void ApplyExtended(StatsExtendedSummaryDto extended)
    {
        ApplySemesterComparison(extended.SemesterComparison);
        ApplyProgramComparison(extended.ProgramComparison);
    }
}
