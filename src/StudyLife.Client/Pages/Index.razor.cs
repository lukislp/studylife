using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    private string _greeting = "";
    private string _motivation = "";
    private List<StudySession> _todaySessions = new();
    private StudySession? _activeSession;
    private StudySession? _upcomingSession;
    private List<CourseDto> _courses = new();
    private Dictionary<int, string?> _courseTags = new();
    // Countdown badge next to the course pills: days until CourseGoalDto.TargetDate, only for
    // courses with a goal within the next DashboardSummaryBuilder.CourseDeadlineCutoffDays (same
    // fetch as _upcomingGoals, no extra request). Deliberately UNCAPPED at the low end (negative
    // days = overdue, like DashboardUpcomingGoalsCard) - an elapsed target date without a
    // recorded grade shouldn't silently disappear from the course list just because it's "too old".
    private Dictionary<int, int> _courseDeadlineDays = new();
    private int _weekSessions;
    private string _weekHours = "0h";
    private int _streak;
    private int _longestStreak;

    // Today's plan adherence ("focus score"): studied vs. planned sessions today. Hidden entirely
    // when nothing was planned today, since a ratio against zero is meaningless.
    private bool _focusScoreVisible;
    private double _focusScorePercent;
    private int _focusScoreStudied;
    private int _focusScorePlanned;

    // Weekly quota
    private string _weekQuotaHours = "0h";
    private bool _weekQuotaWarning;
    private double _weekQuotaPercent;
    private double _weekQuotaMinPercent;
    private string _weekQuotaMissing = "";
    private int _weekQuotaMin;
    private int _weekQuotaMax;

    // Monthly quota
    private string _monthHours = "0h";
    private int _monthTargetMin;
    private int _monthTargetMax;
    private double _quotaPercent;
    private double _quotaMinPercent;
    private bool _quotaWarning;
    private string _quotaMissing = "";

    private DashboardQuotaCard.QuotaCardData WeekQuotaData => new(
        "card-quota-week",
        T.ThisWeekQuota,
        _weekQuotaHours,
        string.Format(T.WeekQuotaTargetSuffix ?? "", _weekQuotaMin, _weekQuotaMax),
        _weekQuotaWarning,
        _weekQuotaPercent,
        _weekQuotaMinPercent,
        T.ZeroHours,
        string.Format(T.WeekTargetLegend ?? "", _weekQuotaMin, _weekQuotaMax),
        _weekQuotaWarning ? (MarkupString?)new MarkupString(string.Format(T.WeeklyGoalMissingText ?? "", $"<strong>{_weekQuotaMissing}</strong>")) : null);

    private DashboardQuotaCard.QuotaCardData MonthQuotaData => new(
        "card-quota",
        T.ThisMonthQuota,
        _monthHours,
        string.Format(T.MonthQuotaTargetSuffix ?? "", _monthTargetMin, _monthTargetMax),
        _quotaWarning,
        _quotaPercent,
        _quotaMinPercent,
        T.ZeroHours,
        string.Format(T.MonthTargetLegend ?? "", _monthTargetMin, _monthTargetMax),
        _quotaWarning ? (MarkupString?)new MarkupString(string.Format(T.MonthlyGoalMissingText ?? "", $"<strong>{_quotaMissing}</strong>")) : null);

    private string FocusScoreStatusText => _focusScorePercent >= 100
        ? T.FocusScoreAllOnPlan ?? ""
        : _focusScorePercent >= 50
            ? T.FocusScorePartial ?? ""
            : T.FocusScoreMissed ?? "";

    private string FocusScoreRatioText => string.Format(T.FocusScoreRatioFormat ?? "", _focusScoreStudied, _focusScorePlanned);

    // Forecast text lives inside DashboardProgressCard (Phase 3/goals group), but its value
    // (_forecastAvailable/_forecastAlreadyDone) is only computed in Phase 5 (needs the all-time
    // history) - without this guard the card would show "not enough data" for every user between
    // those two phases, a wrong value that then flips to the real forecast (exactly what the
    // progressive-render rework must avoid).
    private string ForecastText => _achievementsLoading
        ? "…"
        : _forecastAlreadyDone
            ? T.ForecastAlreadyDone ?? ""
            : _forecastAvailable
                ? string.Format(T.ForecastCompletionText ?? "", _forecastDateLabel)
                : T.ForecastNotEnoughData ?? "";

    private List<DashboardTrendChart.WeekTrend> _weeklyTrend = new();

    private List<DashboardUpcomingGoalsCard.UpcomingGoal> _upcomingGoals = new();

    // ECTS & average grade (mirrors Stats.razor's "study progress"/average-grade tiles, same Ects-weighting)
    private int _ectsEarned;
    private int _ectsTotal;
    private double _ectsPercent;
    private string _averageGradeLabel = "–";

    // Today's ring + 7-day streak strip
    private List<DashboardTodayRingCard.DayDot> _streakStrip = new();
    private double _todayRingPercent;
    private string _todayHoursLabel = "0h";
    private string _dailyTargetLabel = "0h";
    // True once today's studied hours reach/exceed the daily target - drives the gold ring color
    // shift in DashboardTodayRingCard. RingPercent itself stays capped at 100 (it's an angle).
    private bool _todayRingExceeded;

    // Week-over-week delta
    private string? _weekDeltaLabel;
    private bool _weekDeltaUp;

    // Recently completed sessions
    private List<StudySession> _recentSessions = new();

    // Shared all-time fetch used by the achievements/month-comparison/best-record/forecast tiles,
    // so all four get the ~10-year lookback without firing separate requests for it. Gated by
    // refreshHeavyHistory (only true reloads triggered by session changes) and by
    // _lastHeavyFetchAt below, since settings-only changes (e.g. a theme switch) and rapid-fire
    // session edits shouldn't each re-run this ~10-year fetch. This field is the unfiltered fetch
    // cache (all programmes); DashboardSummaryBuilder re-scopes it to the ACTIVE programme on
    // every build, so a programme switch (settings-only reload) can change the scope without
    // re-triggering the fetch or permanently narrowing the cache.
    private List<StudySessionDto> _allTimeHistoryRaw = new();
    private DateTime _lastHeavyFetchAt = DateTime.MinValue;
    private static readonly TimeSpan HeavyFetchThrottle = TimeSpan.FromMinutes(5);

    // Inactivity nudge: mirrors InactivityReminderService's exact threshold logic (never studied ->
    // always show; otherwise show once the gap exceeds UserSettings.InactivityThresholdDays), so users
    // see the same signal on-screen even without push notifications enabled.
    private bool _showInactivityNudge;
    private int _daysSinceLastSession;

    // Backup staleness hint: nudges toward the manual offsite download (SetupBackupCard.razor,
    // GET /api/backup/database), NOT toward the automatic weekly server dump
    // (BackgroundTaskService.RunBackupDumpAsync) - that doesn't protect against total device loss,
    // since it ends up on the same machine/Pi. BackupController sets
    // UserSettings.LastBackupDownloadAt on every successful manual download. Only ever shown
    // when rawBackupSupported (api/system/capabilities) is true - in Postgres mode the manual
    // download this hint links to is a 501 dead end (BackupController.IsRawBackupAvailable),
    // and that mode typically already has its own genuinely-offsite protection (CNPG + an
    // external object store, see k8s/02-postgres.yaml) that this hint predates.
    private bool _showBackupStalenessHint;
    private bool _backupNeverDownloaded;
    private int _daysSinceLastBackup;
    // Only the first registered user (owner of the installation) may use the raw .db download
    // this hint links to (BackupController.IsOwnerAsync) - for all other
    // users, LastBackupDownloadAt stays null forever, so the hint would permanently nudge toward an
    // action that structurally can never succeed.
    private bool _isOwner;

    // Topics progress: sums CourseDto.Topics vs. CourseGoalDto.CompletedTopics across active
    // (selected, not-yet-completed) courses only - surfaces the Setup.razor Themen-Checkliste
    // on the dashboard. Completed courses are excluded: their topics are moot once the course
    // itself is done, and counting them would inflate the total with courses no longer being worked on.
    private int _topicsCompleted;
    private int _topicsTotal;
    private double _topicsPercent;

    private I18nLanguageWatcher _langWatcher = null!;
    private Task<bool>? _isOwnerTask;

    // Progressive render (2026-09 audit): default true so the FIRST LoadDataAsync run shows a
    // skeleton for the cards each flag covers instead of their zero/empty field defaults. A
    // refresh (OnSessionsChanged/OnSettingsChanged) never resets these back to true - the already
    // rendered data stays visible while newer data replaces it, matching every other page's
    // refresh behaviour (no flicker back to a loading state).
    private bool _sessionsLoading = true;
    private bool _goalsLoading = true;
    private bool _healthLoading = true;
    private bool _achievementsLoading = true;

    protected override Task OnInitializingAsync()
    {
        State.OnSessionsChanged += OnSessionsChanged;
        State.OnSettingsChanged += OnSettingsChanged;
        State.OnServerChanged += OnServerChanged;
        _isOwnerTask = State.GetIsOwnerAsync();
        // LoadDataAsync can only run once T has loaded (it formats labels), but its three
        // biggest fetches are memoized in AppStateService - kicking them off here lets them
        // travel in parallel with the i18n table instead of one full round trip after it
        // (2026-09 audit L6). LoadDataAsync's own awaits then hit already-resolved tasks.
        _ = State.GetSettingsAsync();
        _ = State.GetCoursesAsync();
        _ = State.GetSessionsAsync();
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        _langWatcher = new I18nLanguageWatcher(I18nText);
        await _langWatcher.InitAsync();
        _isOwner = await _isOwnerTask!;
        RefreshGreeting();
        _motivation = GetRandomMotivation();
        _insightVariant = new Random().Next(2);
        await LoadDataAsync(refreshHeavyHistory: true);
    }

    private void RefreshGreeting()
    {
        var hour = DateTime.Now.Hour;
        _greeting = hour < 12 ? T.GoodMorning : hour < 17 ? T.GoodAfternoon : T.GoodEvening;
    }

    /// <summary>Toolbelt.Blazor.I18nText auto-updates T's own fields and re-renders this component
    /// when the active language changes, but _greeting/_motivation were only ever copied from T
    /// once at load time - so a live language switch (no page reload) left them stuck on whatever
    /// language was active back then. Same root cause/fix shape as Focus.razor's
    /// RefreshLocalizedModeNames.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) return;

        // _langWatcher can still be null here: LocalizedComponentBase's OnInitializedAsync awaits
        // the text-table fetch before this component's own OnTextLoadedAsync (where _langWatcher
        // is assigned) even starts, and that first await doesn't complete synchronously, so Blazor
        // renders once - firstRender=true - before OnTextLoadedAsync has run; a later render
        // catches up once it's set.
        if (_langWatcher != null && await _langWatcher.CheckChangedAsync())
        {
            RefreshGreeting();
            _motivation = GetRandomMotivation();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Localized replacement for the old hardcoded-English DefaultData.ClaudeMotivations
    /// list - picks one of the 12 quotes from IndexText, translated per the active language.</summary>
    private string GetRandomMotivation()
    {
        string[] quotes =
        [
            T.Quote1, T.Quote2, T.Quote3, T.Quote4, T.Quote5, T.Quote6,
            T.Quote7, T.Quote8, T.Quote9, T.Quote10, T.Quote11, T.Quote12,
        ];
        return quotes[new Random().Next(quotes.Length)];
    }

    private async Task LoadDataAsync(bool refreshHeavyHistory)
    {
        // All of the fetches below are independent of each other - only the computation further
        // down needs their results, not the fetches themselves. Starting every task immediately
        // instead of `await`ing each one in turn (the previous shape) turns ~9 sequential round
        // trips into one round trip's worth of wall-clock time (roughly the slowest single
        // request instead of the sum of all of them) - imperceptible on a low-latency LAN, but
        // adds up to multiple real seconds once server round-trip time is non-trivial (e.g. the
        // public demo). dueForHeavyRefresh only touches local fields/the method parameter (no
        // I/O), so it's safe to compute this early too, to decide whether to start that fetch.
        //
        // The awaits below are grouped into phases instead of one long sequential chain (2026-09
        // progressive-render audit): each phase renders (StateHasChanged) as soon as its own data
        // is ready, so the page fills in card by card instead of staying blank until the slowest
        // fetch (GET api/sessions/history, ~500ms measured on a phone) has returned. The tasks
        // themselves already started above/below regardless of phase order, so this only changes
        // WHEN each result is awaited/rendered, never what is fetched.
        //
        // Every number below comes from DashboardSummaryBuilder (StudyLife.Shared), which the
        // server can run against the same inputs - this method only fetches, hands the raw inputs
        // over, and copies the result into the fields the markup binds to. The builder is called
        // once per phase (rather than once for everything) precisely so those phase boundaries
        // survive: a single call would have to wait for the ~10-year history before showing
        // anything at all.
        var settingsTask = State.GetSettingsAsync();
        var coursesTask = State.GetCoursesAsync();
        var hrvTask = Health.IsAvailable ? Health.GetRecentHrvAsync(30) : Task.FromResult<IReadOnlyList<double>?>(null);
        var sleepNightsTask = Health.IsAvailable ? Health.GetRecentSleepNightsAsync(30) : Task.FromResult<IReadOnlyList<SleepNight>?>(null);
        // One wall-clock read for the whole build: the server runs DashboardSummaryBuilder with
        // exactly this instant, so its numbers equal what the fallback below would compute here.
        var now = DateTime.Now;
        // Server path (2026-09): GET api/dashboard/summary returns the complete builder output in
        // one small response instead of the six raw fetches (sessions, two history windows,
        // goals, programmes, notes - up to ~1.5 MB and dozens of LINQ passes in WASM for an older
        // account). Started alongside settings/courses so phase 1 still paints as early as before.
        var summaryTask = TryFetchSummaryAsync(now);

        // ── Phase 1: cheapest prerequisites - settings + courses. Everything below depends on at
        // least one of these two, so this is the earliest the page can show anything real (the
        // course list itself, plus the active-programme scope needed by every later computation).
        var settings = await settingsTask;
        var allCourses = await coursesTask;
        var settingsDto = AppStateService.ToDto(settings);
        _courses = DashboardSummaryBuilder.BuildCourseList(settingsDto, allCourses);
        await InvokeAsync(StateHasChanged);

        var summary = await summaryTask;
        if (summary != null)
        {
            // Same phase boundaries as the fallback below, so the tiles appear in the same order
            // and the loading flags keep their meaning - only the data now comes in one piece.
            ApplySessionsSummary(summary.Sessions);
            _sessionsLoading = false;
            await InvokeAsync(StateHasChanged);

            ApplyGoalsSummary(summary.Goals);
            _goalsLoading = false;
            await InvokeAsync(StateHasChanged);

            BuildReadinessScore(await hrvTask);
            BuildSleepConsistency(await sleepNightsTask);
            _healthLoading = false;
            await InvokeAsync(StateHasChanged);

            ApplyProgressSummary(summary.Progress);
            _achievementsLoading = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // ── Fallback: raw fetches + the same builder locally. Reached offline (AppStateService
        // serves its read caches) or against a server without api/dashboard/summary. The fetches
        // start here rather than at the top so the server path never issues them.
        var sessionsTask = State.GetSessionsAsync();
        var historyTask = State.GetHistoryAsync(DashboardSummaryBuilder.HistoryDays, onlyCompleted: false);
        var isDemoTask = State.GetIsDemoAsync();
        // Same capability flag Setup.razor already uses to hide the raw-backup UI on Postgres
        // (SetupBackupCard/SetupRestoreCard) - the dashboard staleness hint below needs it too
        // (see the field comment on _showBackupStalenessHint for why).
        var capabilitiesTask = Http.GetFromJsonAsync<SystemCapabilitiesResponseDto>(
            $"api/system/capabilities?nocache={DateTime.UtcNow.Ticks}", StudyLifeJson.Options);
        var goalsTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        var groupQuotasTask = State.GetActiveGroupQuotasAsync();
        var studyProgramsTask = State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms");
        var dueForHeavyRefresh = _lastHeavyFetchAt == DateTime.MinValue || DateTime.UtcNow - _lastHeavyFetchAt >= HeavyFetchThrottle;
        var heavyHistoryTask = refreshHeavyHistory && dueForHeavyRefresh
            ? State.GetHistoryAsync(DashboardSummaryBuilder.AchievementHistoryDays)
            : null;

        // The builder's input is filled in phase by phase, in the same order the page awaits its
        // fetches - each phase only reads the fields its own group needs (see the phase methods).
        var input = new DashboardSummaryInput
        {
            Settings = settingsDto,
            AllCourses = allCourses,
            IsOwner = _isOwner,
        };

        // ── Phase 2: sessions/history-driven tiles (today/next session, week stats, quotas,
        // trend, today ring, recent sessions, donut, neglected course, insights, latest note).
        input.Sessions = (await sessionsTask).Select(AppStateService.ToDto).ToList();
        input.Now = now;
        input.History = await historyTask ?? new();
        input.IsDemo = await isDemoTask;
        // Fail-open to `true` (still show the backup hint) if the capabilities fetch itself
        // fails, same as everywhere else this flag is read.
        try
        {
            var capabilities = await capabilitiesTask;
            if (capabilities is not null) input.RawBackupSupported = capabilities.RawBackupSupported;
        }
        catch { /* older server or transient error - fail open, same as Setup.razor's own fetch */ }
        input.Notes = await State.GetJsonCachedAsync<List<NoteDto>>("api/notes") ?? new();

        var sessionsSummary = DashboardSummaryBuilder.BuildSessions(input);
        ApplySessionsSummary(sessionsSummary);

        _sessionsLoading = false;
        await InvokeAsync(StateHasChanged);

        // ── Phase 3: goals/programs/quotas (upcoming goals, ECTS/avg grade, topics progress).
        // The study-programme list is small/cheap enough to refetch on every reload instead of
        // hiding it behind refreshHeavyHistory; it is only consumed later by the achievements
        // group (phase 5) but belongs conceptually with the other goals/programs/quotas data.
        input.Goals = await goalsTask ?? new();
        input.GroupQuotas = await groupQuotasTask;
        input.StudyPrograms = await studyProgramsTask ?? new();

        var goalsSummary = DashboardSummaryBuilder.BuildGoals(input);
        ApplyGoalsSummary(goalsSummary);

        _goalsLoading = false;
        await InvokeAsync(StateHasChanged);

        // ── Phase 4: health tiles (HealthKit HRV/sleep - native-app-only, can be genuinely slow
        // on-device; resolves instantly to null on the web client, see hrvTask/sleepNightsTask
        // above). Deliberately NOT part of the shared builder: native health data never leaves
        // the device.
        BuildReadinessScore(await hrvTask);
        BuildSleepConsistency(await sleepNightsTask);

        _healthLoading = false;
        await InvokeAsync(StateHasChanged);

        // ── Phase 5: achievements/heavy history. Deliberately a separate, much longer-range fetch
        // than the phase-2 history above - achievements and the month/year comparison are meant to
        // reflect the whole journey, not just the last ~13 months. Only redone when
        // refreshHeavyHistory is requested (session data actually changed, not a settings-only
        // change) AND at least HeavyFetchThrottle has passed since the last fetch (or this is the
        // first load) - a second safety net so rapid-fire session edits (e.g. several Focus Timer
        // sessions in a row) don't each re-hammer the ~10-year endpoint. When the throttle skips
        // it, the retained cache from the previous fetch is handed to the builder unchanged.
        if (heavyHistoryTask != null)
        {
            _allTimeHistoryRaw = await heavyHistoryTask ?? new();
            _lastHeavyFetchAt = DateTime.UtcNow;
        }
        input.HeavyHistory = _allTimeHistoryRaw;

        ApplyProgressSummary(DashboardSummaryBuilder.BuildProgress(input, sessionsSummary, goalsSummary));

        _achievementsLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// GET api/dashboard/summary for the given client instant. Returns null on any failure
    /// (offline, older server, transient error) so the caller falls back to the raw fetches and
    /// the local builder; deliberately no read cache of its own, because the fallback already
    /// computes from AppStateService's cached raw data with a fresh "now", which is the more
    /// accurate offline answer than a summary frozen at an earlier instant.
    /// </summary>
    private async Task<DashboardSummaryDto?> TryFetchSummaryAsync(DateTime now)
    {
        try
        {
            return await Http.GetFromJsonAsync<DashboardSummaryDto>(
                $"api/dashboard/summary?now={now:yyyy-MM-ddTHH:mm:ss}", StudyLifeJson.Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Phase 2 result -> the fields the markup binds to. Only the localized strings are
    /// assembled here; every number/label already comes formatted from the builder.</summary>
    private void ApplySessionsSummary(DashboardSessionsSummaryDto s)
    {
        _todaySessions = s.TodaySessions.Select(ToSession).ToList();
        _activeSession = s.ActiveSession == null ? null : ToSession(s.ActiveSession);
        _upcomingSession = s.UpcomingSession == null ? null : ToSession(s.UpcomingSession);

        _weekSessions = s.WeekSessions;
        _weekHours = s.WeekHoursLabel;
        _streak = s.Streak;
        _longestStreak = s.LongestStreak;

        _focusScoreVisible = s.FocusScore.Visible;
        _focusScorePercent = s.FocusScore.Percent;
        _focusScoreStudied = s.FocusScore.Studied;
        _focusScorePlanned = s.FocusScore.Planned;

        _showInactivityNudge = s.Inactivity.Show;
        _daysSinceLastSession = s.Inactivity.DaysSinceLastSession;

        _showBackupStalenessHint = s.BackupHint.Show;
        _backupNeverDownloaded = s.BackupHint.NeverDownloaded;
        _daysSinceLastBackup = s.BackupHint.DaysSinceLastBackup;

        _weekQuotaMin = s.WeekQuota.TargetMin;
        _weekQuotaMax = s.WeekQuota.TargetMax;
        _weekQuotaPercent = s.WeekQuota.Percent;
        _weekQuotaMinPercent = s.WeekQuota.MinPercent;
        _weekQuotaWarning = s.WeekQuota.Warning;
        _weekQuotaHours = s.WeekQuota.HoursLabel;
        _weekQuotaMissing = s.WeekQuota.MissingLabel;

        _monthTargetMin = s.MonthQuota.TargetMin;
        _monthTargetMax = s.MonthQuota.TargetMax;
        _quotaPercent = s.MonthQuota.Percent;
        _quotaMinPercent = s.MonthQuota.MinPercent;
        _quotaWarning = s.MonthQuota.Warning;
        _monthHours = s.MonthQuota.HoursLabel;
        _quotaMissing = s.MonthQuota.MissingLabel;

        _weeklyTrend = s.WeeklyTrend
            .Select(w => new DashboardTrendChart.WeekTrend(w.Label, w.Hours, w.Percent, w.IsCurrent))
            .ToList();
        _weekDeltaLabel = s.WeekDeltaLabel;
        _weekDeltaUp = s.WeekDeltaUp;

        _recentSessions = s.RecentSessions.Select(ToSession).ToList();

        _todayRingPercent = s.TodayRing.RingPercent;
        _todayRingExceeded = s.TodayRing.Exceeded;
        _todayHoursLabel = s.TodayRing.HoursLabel;
        _dailyTargetLabel = s.TodayRing.DailyTargetLabel;
        _streakStrip = s.StreakStrip
            .Select(d => new DashboardTodayRingCard.DayDot(d.Label, d.Studied, d.IsToday))
            .ToList();

        ApplyMiniDonut(s.MiniDonut);
        ApplyNeglectedCourse(s.NeglectedCourse);
        ApplyInsights(s.ProductivityHint, s.WeekdayInsight);
        ApplyAnomalyHint(s.AnomalyHint);
        ApplyLatestNote(s.LatestNote);
    }

    /// <summary>Phase 3 result -> fields.</summary>
    private void ApplyGoalsSummary(DashboardGoalsSummaryDto g)
    {
        _courseTags = g.CourseTags;
        _courseDeadlineDays = g.CourseDeadlineDays;
        _upcomingGoals = g.UpcomingGoals
            .Select(x => new DashboardUpcomingGoalsCard.UpcomingGoal(x.CourseName, x.TargetDate))
            .ToList();

        _ectsEarned = g.EctsEarned;
        _ectsTotal = g.EctsTotal;
        _ectsPercent = g.EctsPercent;
        _averageGradeLabel = g.AverageGradeLabel;

        _topicsCompleted = g.TopicsCompleted;
        _topicsTotal = g.TopicsTotal;
        _topicsPercent = g.TopicsPercent;
    }

    /// <summary>Maps a shared session DTO back to the client model the dashboard cards take.</summary>
    private static StudySession ToSession(StudySessionDto d) => new()
    {
        Id = d.Id,
        CourseId = d.CourseId,
        CourseName = d.CourseName,
        CourseColor = d.CourseColor,
        StartTime = d.StartTime,
        EndTime = d.EndTime,
        Topic = d.Topic,
        Notes = d.Notes,
        IsCompleted = d.IsCompleted,
        TimerModeId = d.TimerModeId,
        RecurrenceGroupId = d.RecurrenceGroupId,
    };

    private void OnSessionsChanged() => InvokeAsync(async () =>
    {
        await LoadDataAsync(refreshHeavyHistory: true);
        StateHasChanged();
    });

    // Notes, course goals, programmes and anything else the summary depends on: another device
    // changed it, reload the summary (sessions/settings have their own events above).
    private void OnServerChanged(string? kind) => InvokeAsync(async () =>
    {
        await LoadDataAsync(refreshHeavyHistory: false);
        StateHasChanged();
    });

    private void OnSettingsChanged() => InvokeAsync(async () =>
    {
        await LoadDataAsync(refreshHeavyHistory: false);
        StateHasChanged();
    });

    public void Dispose()
    {
        State.OnSessionsChanged -= OnSessionsChanged;
        State.OnSettingsChanged -= OnSettingsChanged;
        State.OnServerChanged -= OnServerChanged;
    }
}
