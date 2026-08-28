using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    /// <summary>One point of the weekly-trend chart data. Record struct instead of a plain value
    /// tuple - see the trendData comment in LoadDataAsync for why.</summary>
    private readonly record struct WeekTrendPoint(DateTime Start, double Hours);

    private string _greeting = "";
    private string _motivation = "";
    private List<StudySession> _todaySessions = new();
    private StudySession? _activeSession;
    private StudySession? _upcomingSession;
    private List<CourseDto> _courses = new();
    private Dictionary<int, string?> _courseTags = new();
    // Countdown badge next to the course pills: days until CourseGoalDto.TargetDate, only for
    // courses with a goal within the next CourseDeadlineCutoffDays - more distant dates aren't
    // actionable yet and would just clutter the course list (same fetch as _upcomingGoals,
    // no extra request). Deliberately UNCAPPED at the low end (negative days = overdue, like
    // DashboardUpcomingGoalsCard) - an elapsed target date without a recorded grade shouldn't
    // silently disappear from the course list just because it's "too old".
    private Dictionary<int, int> _courseDeadlineDays = new();
    private const int CourseDeadlineCutoffDays = 60;
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
    // shift in DashboardTodayRingCard (Task 5). RingPercent itself stays capped at 100 (it's an angle).
    private bool _todayRingExceeded;

    // Week-over-week delta
    private string? _weekDeltaLabel;
    private bool _weekDeltaUp;

    // Recently completed sessions
    private List<StudySession> _recentSessions = new();

    // Shared long-range history for everything that looks further back than the ±7/90-day
    // AppStateService cache covers (month quota, 8-week trend, streak, recent sessions, mini-donut,
    // neglected-course) - see /api/sessions/history. `sessions` (AppStateService) stays the source for
    // near-term data (today/active/upcoming), which it already covers correctly.
    private const int HistoryDays = 400;

    // Shared all-time fetch used by BuildAchievements and BuildMonthComparison, so both get the
    // ~10-year lookback without firing two separate requests for it. Gated by refreshHeavyHistory
    // (only true reloads triggered by session changes) and by _lastHeavyFetchAt below, since settings-only
    // changes (e.g. a theme switch) and rapid-fire session edits shouldn't each re-run this ~10-year fetch.
    // _allTimeHistoryRaw = unfiltered fetch cache (all programmes); _allTimeHistory = the
    // view cheaply re-filtered from it on every reload, scoped to the ACTIVE programme,
    // that BuildAchievements/BuildMonthComparison/BuildBestRecords/BuildForecast consume. The
    // separation exists so a programme switch (settings-only reload) can change the scope
    // without re-triggering the ~10-year fetch or permanently narrowing the cache.
    private List<StudySessionDto> _allTimeHistoryRaw = new();
    private List<StudySessionDto> _allTimeHistory = new();
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
    private const int BackupStalenessThresholdDays = 45;
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

    protected override Task OnInitializingAsync()
    {
        State.OnSessionsChanged += OnSessionsChanged;
        State.OnSettingsChanged += OnSettingsChanged;
        _isOwnerTask = State.GetIsOwnerAsync();
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
        var settingsTask = State.GetSettingsAsync();
        var coursesTask = State.GetCoursesAsync();
        var sessionsTask = State.GetSessionsAsync();
        var historyTask = State.GetJsonCachedAsync<List<StudySessionDto>>($"api/sessions/history?days={HistoryDays}&onlyCompleted=false");
        var isDemoTask = State.GetIsDemoAsync();
        // Same capability flag Setup.razor already uses to hide the raw-backup UI on Postgres
        // (SetupBackupCard/SetupRestoreCard) - the dashboard staleness hint below needs it too
        // (see the field comment on _showBackupStalenessHint for why).
        var capabilitiesTask = Http.GetFromJsonAsync<SystemCapabilitiesResponseDto>(
            $"api/system/capabilities?nocache={DateTime.UtcNow.Ticks}");
        var goalsTask = State.GetJsonCachedAsync<List<CourseGoalDto>>("api/coursegoals");
        var groupQuotasTask = State.GetActiveGroupQuotasAsync();
        var studyProgramsTask = State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms");
        var hrvTask = Health.IsAvailable ? Health.GetRecentHrvAsync(30) : Task.FromResult<IReadOnlyList<double>?>(null);
        var sleepOnsetTask = Health.IsAvailable ? Health.GetRecentSleepOnsetMinutesAsync(30) : Task.FromResult<IReadOnlyList<double>?>(null);
        var dueForHeavyRefresh = _lastHeavyFetchAt == DateTime.MinValue || DateTime.UtcNow - _lastHeavyFetchAt >= HeavyFetchThrottle;
        var heavyHistoryTask = refreshHeavyHistory && dueForHeavyRefresh
            ? State.GetJsonCachedAsync<List<StudySessionDto>>($"api/sessions/history?days={AchievementHistoryDays}")
            : null;

        var settings = await settingsTask;
        var allCourses = await coursesTask;
        _courses = allCourses.Where(c => settings.SelectedCourseIds.Contains(c.Id)).ToList();

        // Active-programme scope: allCourses is already limited to the active programme
        // (AppStateService.GetCoursesAsync), so this id set defines which sessions belong to "this"
        // programme. ALL session-based tiles/charts below are filtered through it,
        // so switching programmes also switches heatmap/weekly hours/streak & co. - not just
        // the course list and ECTS. Custom course ids never collide with the built-in
        // catalog (1-62) thanks to CustomCourseIdOffset (100000+), so the filter is unambiguous.
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();

        // Also scope the near-term data (today/active/upcoming): a session from another
        // programme showing up as "today's session" would be just as confusing as its history in the charts.
        var sessions = (await sessionsTask).Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        var today = DateTime.Today;
        _todaySessions = sessions.Where(s => s.StartTime.Date == today).OrderBy(s => s.StartTime).ToList();

        // Deliberately NOT State.GetActiveSessionAsync()/GetUpcomingSessionAsync(): those return the
        // first matching session ACROSS ALL programmes; here the same logic runs on the scoped list,
        // so e.g. "up next" shows the next session of THIS programme.
        var nowForScope = DateTime.Now;
        _activeSession = sessions.FirstOrDefault(s => !s.IsCompleted && s.StartTime <= nowForScope && s.EndTime >= nowForScope);
        _upcomingSession = sessions.Where(s => !s.IsCompleted && s.StartTime > nowForScope).OrderBy(s => s.StartTime).FirstOrDefault();

        // Long-range history (all sessions, not just completed - same semantics as the AppStateService
        // cache, just further back) for anything that looks beyond the ±7/90-day window: month quota,
        // 8-week trend, streak, recent sessions, mini-donut, neglected-course.
        // historyAllPrograms stays unscoped for the inactivity nudge below; everything else uses
        // `history`, filtered to the active programme.
        var historyAllPrograms = await historyTask ?? new();
        var history = historyAllPrograms.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        // "Studied" = timer-completed OR the scheduled time has simply passed - see StudyMetrics.IsStudied.
        var now = DateTime.Now;
        var completedHistory = history.Where(s => StudyMetrics.IsStudied(s, now)).ToList();

        var weekStart = StudyMetrics.WeekStartOf(today);
        var weekEnd = weekStart.AddDays(7);
        var weekSessions = history.Where(s => s.StartTime.Date >= weekStart && s.StartTime.Date < weekEnd).ToList();
        _weekSessions = weekSessions.Count;
        var totalMinutes = weekSessions.Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
        _weekHours = $"{Math.Floor(totalMinutes / 60)}h{(totalMinutes % 60 > 0 ? $" {(int)(totalMinutes % 60)}m" : "")}";
        _streak = StudyMetrics.CalcStreak(completedHistory.Select(s => s.StartTime), today);
        _longestStreak = StudyMetrics.CalcLongestStreak(completedHistory.Select(s => s.StartTime));

        // Focus score: today's plan adherence, studied (same "studied" definition as completedHistory
        // above) vs. planned sessions today. Meaningless with zero planned sessions -> hide the card.
        _focusScorePlanned = _todaySessions.Count;
        _focusScoreStudied = _todaySessions.Count(s => s.IsCompleted || s.EndTime <= DateTime.Now);
        _focusScoreVisible = _focusScorePlanned > 0;
        _focusScorePercent = _focusScoreVisible ? Math.Min(100.0, _focusScoreStudied / (double)_focusScorePlanned * 100) : 0;

        // Inactivity nudge deliberately UNSCOPED (historyAllPrograms): it mirrors the server-side
        // InactivityReminderService, which is programme-agnostic - "have you studied at all?" shouldn't
        // fire just because the last studying happened for a different programme.
        var lastPastSession = historyAllPrograms.Where(s => s.StartTime <= DateTime.Now).OrderByDescending(s => s.StartTime).FirstOrDefault();
        var inactivityThreshold = settings.InactivityThresholdDays > 0 ? settings.InactivityThresholdDays : 5;
        if (lastPastSession == null)
        {
            _daysSinceLastSession = inactivityThreshold;
            _showInactivityNudge = true;
        }
        else
        {
            _daysSinceLastSession = (today - lastPastSession.StartTime.Date).Days;
            _showInactivityNudge = _daysSinceLastSession > inactivityThreshold;
        }

        // Backup staleness hint (manual offsite download, see field comment above) - only for
        // the owner, see the _isOwner field comment. Suppressed on public demo instances:
        // the demo user IS the owner and never has a backup timestamp, so the banner would
        // permanently nag every visitor about backing up throwaway seed data - and the
        // backup endpoints are 403-blocked there anyway. ALSO suppressed whenever
        // rawBackupSupported is false (Postgres mode): GET/POST api/backup/database(/encrypted)
        // - the only actions that ever advance LastBackupDownloadAt - return 501 there
        // (BackupController.IsRawBackupAvailable), so the hint would otherwise nag toward an
        // action this deployment structurally cannot perform. A Postgres install typically
        // means CNPG's own continuous WAL archiving + daily backups to an external object
        // store (see k8s/02-postgres.yaml) already cover what this hint originally existed for
        // (protection against total device loss) - fail-open to `true` (still show it) if the
        // capabilities fetch itself fails, same as everywhere else this flag is read.
        var isDemo = await isDemoTask;
        var rawBackupSupported = true;
        try
        {
            var capabilities = await capabilitiesTask;
            if (capabilities is not null) rawBackupSupported = capabilities.RawBackupSupported;
        }
        catch { /* older server or transient error - fail open, same as Setup.razor's own fetch */ }

        if (settings.LastBackupDownloadAt == null)
        {
            _backupNeverDownloaded = true;
            _daysSinceLastBackup = 0;
            _showBackupStalenessHint = _isOwner && !isDemo && rawBackupSupported;
        }
        else
        {
            _backupNeverDownloaded = false;
            _daysSinceLastBackup = (today - settings.LastBackupDownloadAt.Value.Date).Days;
            _showBackupStalenessHint = _isOwner && !isDemo && rawBackupSupported && _daysSinceLastBackup > BackupStalenessThresholdDays;
        }

        // Weekly quota (configurable target, default 25-30 h/week)
        var weekMin = settings.WeeklyGoalMinHours;
        var weekMax = settings.WeeklyGoalMaxHours;
        _weekQuotaMin = weekMin;
        _weekQuotaMax = weekMax;
        var weekHoursVal = totalMinutes / 60.0;
        var weekQuota = StudyMetrics.CalcQuota(weekHoursVal, weekMin, weekMax);
        _weekQuotaPercent = weekQuota.Percent;
        _weekQuotaMinPercent = weekQuota.MinPercent;
        _weekQuotaWarning = weekQuota.Warning;
        var wH = (int)Math.Floor(weekHoursVal);
        var wM = (int)((weekHoursVal - wH) * 60);
        _weekQuotaHours = $"{wH}h{(wM > 0 ? $" {wM}m" : "")}";
        if (_weekQuotaWarning)
        {
            var wmH = (int)Math.Floor(weekQuota.MissingHours);
            var wmM = (int)((weekQuota.MissingHours - wmH) * 60);
            _weekQuotaMissing = wmM > 0 ? $"{wmH}h {wmM}m" : $"{wmH}h";
        }

        // Monthly quota: absolute monthly goal (settings.MonthlyGoalMinHours/MaxHours, independently
        // configurable from the weekly goal - see Setup.razor's monthly-goal card). Deliberately
        // NOT prorated (anymore): the card previously showed the elapsed-weeks share via
        // StudyMetrics.ProrateMonthlyTarget, which made the displayed target (e.g. "20-26 h")
        // contradict the configured goal ("100-130 h") for most of the month and read as a bug.
        // The full goal now applies to the label, the bar, and the warning alike - early-month
        // progress simply shows as a small fill against the whole month's target.
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthSessions = history.Where(s => s.StartTime.Date >= monthStart).ToList();
        var monthMinutes = monthSessions.Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
        var monthHoursVal = monthMinutes / 60.0;

        _monthTargetMin = settings.MonthlyGoalMinHours;
        _monthTargetMax = settings.MonthlyGoalMaxHours;
        var monthQuota = StudyMetrics.CalcQuota(monthHoursVal, _monthTargetMin, _monthTargetMax);
        _quotaPercent = monthQuota.Percent;
        _quotaMinPercent = monthQuota.MinPercent;
        _quotaWarning = monthQuota.Warning;

        var hFull = (int)Math.Floor(monthHoursVal);
        var mRest = (int)((monthHoursVal - hFull) * 60);
        _monthHours = $"{hFull}h{(mRest > 0 ? $" {mRest}m" : "")}";

        if (_quotaWarning)
        {
            var mH = (int)Math.Floor(monthQuota.MissingHours);
            var mM = (int)((monthQuota.MissingHours - mH) * 60);
            _quotaMissing = mM > 0 ? $"{mH}h {mM}m" : $"{mH}h";
        }

        // Weekly trend (last 8 weeks, same "all sessions" semantics as the week_hours tile above)
        // Record struct instead of a value tuple: LINQ (.Max/.Select below) over a List<(...)> of
        // value tuples has triggered a Mono AOT crash at compile time (not call time) in the
        // native app shell (studylife-app, BlazorWebView) that links this same Client project -
        // see project_studylife_app_ios_aot_linq_tuple_crash. A record struct sidesteps it while
        // keeping the same positional-deconstruction ergonomics.
        const int trendWeeks = 8;
        var trendData = new List<WeekTrendPoint>();
        for (var i = trendWeeks - 1; i >= 0; i--)
        {
            var wStart = weekStart.AddDays(-7 * i);
            var wEnd = wStart.AddDays(7);
            var hours = history
                .Where(s => s.StartTime.Date >= wStart && s.StartTime.Date < wEnd)
                .Sum(s => (s.EndTime - s.StartTime).TotalMinutes) / 60.0;
            trendData.Add(new WeekTrendPoint(wStart, hours));
        }
        var maxTrendHours = Math.Max(1, trendData.Max(t => t.Hours));
        _weeklyTrend = trendData
            .Select(t => new DashboardTrendChart.WeekTrend(
                t.Start.ToString("dd.MM"),
                t.Hours,
                Math.Min(100, t.Hours / maxTrendHours * 100),
                t.Start == weekStart))
            .ToList();

        // Week-over-week delta (current week vs. the one right before it, from the same trend data)
        var lastWeekHoursVal = trendData[^2].Hours;
        var deltaHours = weekHoursVal - lastWeekHoursVal;
        _weekDeltaUp = deltaHours >= 0;
        var absDelta = Math.Abs(deltaHours);
        var dH = (int)Math.Floor(absDelta);
        var dM = (int)((absDelta - dH) * 60);
        _weekDeltaLabel = $"{dH}h{(dM > 0 ? $" {dM}m" : "")}";

        // Recently completed sessions (quick "what did I last study" recall)
        _recentSessions = completedHistory
            .OrderByDescending(s => s.StartTime)
            .Take(5)
            .Select(FromHistoryDto)
            .ToList();

        // Active-programme scope: goals/grades from other programmes must not factor into either
        // the average grade or the upcoming deadlines (same activeCourseIds set
        // as above for history/sessions).
        var goals = (await goalsTask ?? new())
            .Where(g => activeCourseIds.Contains(g.CourseId))
            .ToList();
        _courseTags = goals.ToDictionary(g => g.CourseId, g => g.Tag);
        _courseDeadlineDays = goals
            .Where(g => g.TargetDate.HasValue && g.CompletedAt == null)
            .Select(g => new { g.CourseId, Days = (g.TargetDate!.Value.Date - today).Days })
            .Where(x => x.Days <= CourseDeadlineCutoffDays)
            .ToDictionary(x => x.CourseId, x => x.Days);
        _upcomingGoals = StudyMetrics.CalcUpcomingCourseGoals(goals, today)
            .Select(g => new DashboardUpcomingGoalsCard.UpcomingGoal(g.CourseName, g.TargetDate))
            .ToList();

        // ECTS & average grade (mirrors Stats.razor's Ects-weighted calculation).
        // Programme-aware: the group quotas of the ACTIVE programme (built-in: the static
        // CourseCatalog.GroupEctsQuotas; custom: fetched per programme via AppStateService).
        var groupQuotas = await groupQuotasTask;
        _ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, groupQuotas);
        _ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, groupQuotas);
        _ectsPercent = _ectsTotal > 0 ? Math.Min(100.0, _ectsEarned / (double)_ectsTotal * 100) : 0;

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        _averageGradeLabel = averageGrade.HasValue ? StudyMetrics.FormatGrade(averageGrade.Value) : "–";

        var topics = StudyMetrics.CalcTopicsProgress(allCourses, settings.SelectedCourseIds, goals);
        _topicsCompleted = topics.Completed;
        _topicsTotal = topics.Total;
        _topicsPercent = topics.Percent;

        // Today's ring - same "all sessions, not IsCompleted-filtered" semantics as the week/trend tiles above.
        // Daily target derived from the weekly quota (25-30h/week ÷ 7).
        var todayMinutes = _todaySessions.Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
        var todayHoursVal = todayMinutes / 60.0;
        var dailyTargetHours = (weekMin + weekMax) / 2.0 / 7.0;
        var todayRingPercentRaw = todayHoursVal / dailyTargetHours * 100;
        _todayRingPercent = Math.Min(100, todayRingPercentRaw);
        _todayRingExceeded = todayRingPercentRaw >= 100;
        var tH = (int)Math.Floor(todayHoursVal);
        var tM = (int)((todayHoursVal - tH) * 60);
        _todayHoursLabel = $"{tH}h{(tM > 0 ? $" {tM}m" : "")}";
        _dailyTargetLabel = $"{dailyTargetHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}h";

        // 7-day streak strip (studied = at least one completed session that day)
        _streakStrip = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(day => new DashboardTodayRingCard.DayDot(
                day.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture),
                completedHistory.Any(s => s.StartTime.Date == day),
                day == today))
            .ToList();

        BuildMiniDonut(completedHistory, allCourses);
        BuildNeglectedCourse(settings, allCourses, completedHistory);
        BuildProductivityHint(completedHistory, settings, _todaySessions);
        BuildWeekdayInsight(completedHistory);
        BuildAnomalyHint(completedHistory);
        // Rotation (Task 4): if the weekday variant was picked for this page load AND has enough
        // data, it replaces the time-of-day insight's output wholesale. BuildProductivityHint above
        // is never modified for this - its own visibility/planned/status/plan-link logic stays intact
        // as the fallback whenever the weekday variant isn't available.
        if (_insightVariant == 1 && _weekdayInsightAvailable)
        {
            _productivityHintVisible = true;
            _productivityInsight = _weekdayInsightText;
            _productivityPlanned = false;
            _productivityStatus = null;
            _productivityShowPlanLink = false;
        }
        await BuildLatestNoteAsync(allCourses);
        BuildReadinessScore(await hrvTask);
        BuildSleepConsistency(await sleepOnsetTask);

        // Deliberately a separate, much longer-range fetch than `history` (HistoryDays = 400) above -
        // achievements and the month/year comparison are meant to reflect the whole journey, not just
        // the last ~13 months. Shared between both so it's only fetched once. Only redone when
        // refreshHeavyHistory is requested (session data actually changed, not a settings-only change)
        // AND at least HeavyFetchThrottle has passed since the last fetch (or this is the first load) -
        // a second safety net so rapid-fire session edits (e.g. several Focus Timer sessions in a row)
        // don't each re-hammer the ~10-year endpoint.
        if (heavyHistoryTask != null)
        {
            _allTimeHistoryRaw = await heavyHistoryTask ?? new();
            _lastHeavyFetchAt = DateTime.UtcNow;
        }
        // Scoped to the active programme + rebuilt on EVERY reload (including settings-only, e.g.
        // a programme switch in setup or a changed desired graduation date, detected via poll): the
        // filtering/computing is cheap and in-memory, only the ~10-year fetch above is expensive and
        // therefore stays gated behind refreshHeavyHistory + throttle.
        _allTimeHistory = _allTimeHistoryRaw.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();

        // Programmes-completed achievement: counts across ALL of the user's programmes (deliberately
        // NOT scoped to activeCourseIds - "how many programmes have you completed in total"
        // is by definition a cross-programme milestone). IsCompleted is
        // a purely manual flag (StudyProgramsController), the built-in programme never counts
        // (no DB entry, IsCompleted always false). Small/cheap enough to
        // refetch on every reload instead of hiding it behind refreshHeavyHistory.
        var studyPrograms = await studyProgramsTask ?? new();
        _programsCompleted = studyPrograms.Count(p => p.IsCompleted);

        BuildAchievements(settings, activeCourseIds);
        BuildMonthComparison();
        BuildBestRecords();
        BuildForecast(settings, allCourses, _allTimeHistory);
    }

    private static string FormatHoursLabel(double hours)
    {
        var h = (int)Math.Floor(hours);
        var m = (int)((hours - h) * 60);
        return $"{h}h{(m > 0 ? $" {m}m" : "")}";
    }

    private static StudySession FromHistoryDto(StudySessionDto d) => new()
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
    };

    private void OnSessionsChanged() => InvokeAsync(async () =>
    {
        await LoadDataAsync(refreshHeavyHistory: true);
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
    }
}
