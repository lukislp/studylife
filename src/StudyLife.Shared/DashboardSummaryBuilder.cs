using System.Globalization;

namespace StudyLife.Shared;

/// <summary>
/// The raw inputs the dashboard has after its fetches - exactly what Index.razor.cs's
/// LoadDataAsync loads, mapped to shared types so the server can assemble the same input from
/// the database. <see cref="Now"/> is the caller's wall clock: nothing in
/// <see cref="DashboardSummaryBuilder"/> ever reads DateTime.Now/Today itself, so the result is
/// a pure function of this object (and therefore cacheable and testable).
/// </summary>
public class DashboardSummaryInput
{
    public UserSettingsDto Settings { get; set; } = new();

    /// <summary>All courses of the ACTIVE study programme (GET /api/courses is already
    /// programme-scoped). Defines the id set every session/goal below is filtered through.</summary>
    public List<CourseDto> AllCourses { get; set; } = new();

    /// <summary>The near-term session list (GET /api/sessions), unscoped - the builder applies
    /// the active-programme filter itself.</summary>
    public List<StudySessionDto> Sessions { get; set; } = new();

    /// <summary>Long-range history (GET /api/sessions/history?days=<see cref="HistoryDays"/>&amp;
    /// onlyCompleted=false), unscoped: the inactivity nudge is deliberately programme-agnostic
    /// and needs the unfiltered list.</summary>
    public List<StudySessionDto> History { get; set; } = new();

    /// <summary>All-time history (GET /api/sessions/history?days=<see cref="AchievementHistoryDays"/>,
    /// studied-only), unscoped. Null is tolerated and behaves like an empty list - the client
    /// throttles this fetch and passes its retained cache instead, see LoadDataAsync.</summary>
    public List<StudySessionDto>? HeavyHistory { get; set; }

    public List<CourseGoalDto> Goals { get; set; } = new();

    /// <summary>ECTS quotas per elective group of the active programme.</summary>
    public IReadOnlyDictionary<string, int> GroupQuotas { get; set; } = new Dictionary<string, int>();

    /// <summary>All of the user's study programmes - the completed count is a deliberately
    /// cross-programme milestone.</summary>
    public List<StudyProgramSummaryDto> StudyPrograms { get; set; } = new();

    /// <summary>Notes, newest first (the server sorts by UpdatedAt descending).</summary>
    public List<NoteDto> Notes { get; set; } = new();

    /// <summary>First registered user of the installation - only they can use the raw backup
    /// download the staleness hint links to.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Public demo instance.</summary>
    public bool IsDemo { get; set; }

    /// <summary>GET /api/system/capabilities - false in Postgres mode, where the manual backup
    /// download is a 501 dead end. Fail-open to true when the capability fetch failed.</summary>
    public bool RawBackupSupported { get; set; } = true;

    /// <summary>The caller's wall clock.</summary>
    public DateTime Now { get; set; }

    /// <summary>Derived from <see cref="Now"/> - never a separate clock read.</summary>
    public DateTime Today => Now.Date;
}

/// <summary>
/// Builds the whole dashboard summary (see <see cref="DashboardSummaryDto"/>) from raw inputs.
/// Extracted verbatim from Index.razor.cs's LoadDataAsync and its Index.Achievements/Forecast/
/// Insights partials so that client and server compute identical numbers - same LINQ, same
/// rounding, same ordering, same tie-breaking, same magic constants.
///
/// <see cref="Build"/> produces everything at once (what a server endpoint needs). The three
/// phase methods it composes are public because the page renders progressively: it copies each
/// group into its fields at its own render point, so the tiles keep appearing exactly when they
/// do today instead of all waiting for the slowest (~10-year) fetch.
/// </summary>
public static class DashboardSummaryBuilder
{
    /// <summary>Lookback of the shared long-range history fetch, for everything beyond the
    /// AppStateService cache's ±7/90-day window (month quota, 8-week trend, streak, recent
    /// sessions, mini donut, neglected course).</summary>
    public const int HistoryDays = 400;

    /// <summary>Lookback of the all-time fetch behind achievements, month comparison, best
    /// records and the forecast - the whole journey, not just the last ~13 months.</summary>
    public const int AchievementHistoryDays = 3650;

    /// <summary>Course-pill deadline badges only appear within this window - more distant dates
    /// aren't actionable yet and would just clutter the course list.</summary>
    public const int CourseDeadlineCutoffDays = 60;

    /// <summary>Days without a manual offsite backup download before the hint appears.</summary>
    public const int BackupStalenessThresholdDays = 45;

    /// <summary>Lookback of the course-focus donut.</summary>
    public const int MiniDonutDays = 30;

    /// <summary>Both insights need at least this many studied sessions before a pattern means
    /// anything.</summary>
    public const int ProductivityMinSessions = 10;

    /// <summary>...and the winning bucket/weekday must hold at least this share of the hours.</summary>
    public const double ProductivityMinShare = 0.30;

    private const int AnomalyBaselineWeeks = 8;
    private const int AnomalyMinBaselineWeeks = 4;
    private const double AnomalyThresholdRatio = 0.5;
    private const double AnomalyMinBaselineHours = 1.0;

    /// <summary>Before Wednesday (&lt; 3 elapsed weekdays), even a like-for-like comparison is
    /// still too noisy - a single missed Monday would otherwise immediately scream "50% less".</summary>
    private const int AnomalyMinDaysElapsed = 3;

    /// <summary>Number of bars in the weekly trend chart.</summary>
    private const int TrendWeeks = 8;

    /// <summary>Legend entries of the donut before the rest collapses into an "other" slice.</summary>
    private const int DonutMaxLegend = 4;

    /// <summary>
    /// Time-of-day buckets of the productivity hint, copied 1:1 from Stats.razor's
    /// BuildWeekdayAndTimeOfDay - a session counts entirely toward the bucket of its start hour,
    /// even if it runs past the boundary. DashboardProductivityHintDto.BestBucketIndex indexes
    /// into this array, so the client's localized bucket names must stay in the same order.
    /// </summary>
    public static readonly (int From, int To)[] TimeOfDayBuckets =
        { (0, 6), (6, 9), (9, 12), (12, 15), (15, 18), (18, 21), (21, 24) };

    /// <summary>Everything at once - what a server endpoint serves.</summary>
    public static DashboardSummaryDto Build(DashboardSummaryInput input)
    {
        var sessions = BuildSessions(input);
        var goals = BuildGoals(input);
        return new DashboardSummaryDto
        {
            Courses = BuildCourseList(input.Settings, input.AllCourses),
            Sessions = sessions,
            Goals = goals,
            Progress = BuildProgress(input, sessions, goals),
        };
    }

    /// <summary>Phase 1: the selected courses of the active programme (the course pills). Takes
    /// its two inputs directly rather than the whole <see cref="DashboardSummaryInput"/>, because
    /// the page can already render this from its first two fetches - long before the session
    /// history that everything else needs has arrived.</summary>
    public static List<CourseDto> BuildCourseList(UserSettingsDto settings, IEnumerable<CourseDto> allCourses) =>
        allCourses.Where(c => settings.SelectedCourseIds.Contains(c.Id)).ToList();

    /// <summary>
    /// Active-programme scope: AllCourses is already limited to the active programme, so this id
    /// set defines which sessions belong to "this" programme. Custom course ids never collide
    /// with the built-in catalog (1-62) thanks to CustomCourseIdOffset (100000+), so the filter
    /// is unambiguous.
    /// </summary>
    private static HashSet<int> ActiveCourseIds(DashboardSummaryInput input) =>
        input.AllCourses.Select(c => c.Id).ToHashSet();

    // ── Phase 2: sessions / 400-day history ───────────────────────────────────

    /// <summary>Phase 2: everything driven by the session list and the 400-day history.</summary>
    public static DashboardSessionsSummaryDto BuildSessions(DashboardSummaryInput input)
    {
        var settings = input.Settings;
        var allCourses = input.AllCourses;
        var activeCourseIds = ActiveCourseIds(input);
        var now = input.Now;
        var today = input.Today;

        var result = new DashboardSessionsSummaryDto();

        // Also scope the near-term data (today/active/upcoming): a session from another
        // programme showing up as "today's session" would be just as confusing as its history in
        // the charts.
        var sessions = input.Sessions.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        result.TodaySessions = sessions.Where(s => s.StartTime.Date == today).OrderBy(s => s.StartTime).ToList();

        // Deliberately not the "first match across ALL programmes" variants used elsewhere: here
        // the same logic runs on the scoped list, so e.g. "up next" shows the next session of
        // THIS programme.
        result.ActiveSession = sessions.FirstOrDefault(s => !s.IsCompleted && s.StartTime <= now && s.EndTime >= now);
        result.UpcomingSession = sessions.Where(s => !s.IsCompleted && s.StartTime > now).OrderBy(s => s.StartTime).FirstOrDefault();

        // historyAllPrograms stays unscoped for the inactivity nudge below; everything else uses
        // `history`, filtered to the active programme.
        var historyAllPrograms = input.History;
        var history = historyAllPrograms.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        // "Studied" = timer-completed OR the scheduled time has simply passed.
        var completedHistory = history.Where(s => StudyMetrics.IsStudied(s, now)).ToList();

        var weekStart = StudyMetrics.WeekStartOf(today);
        var weekEnd = weekStart.AddDays(7);
        // Two different questions about the same week: `weekSessions` is everything SCHEDULED in
        // it (the quota tile below - "how much is on the plan?"), `weekStudied` only what has
        // actually been studied (the week stats card, the trend and the delta - "how much did I
        // do?"). See docs/ARCHITECTURE.md "Number semantics".
        var weekSessions = history.Where(s => s.StartTime.Date >= weekStart && s.StartTime.Date < weekEnd).ToList();
        var weekStudied = weekSessions.Where(s => StudyMetrics.IsStudied(s, now)).ToList();
        var weekStudiedHours = weekStudied.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        result.WeekSessions = weekStudied.Count;
        result.WeekHoursLabel = StudyMetrics.FormatHoursMinutes(weekStudiedHours, omitZeroMinutes: true);
        result.Streak = StudyMetrics.CalcStreak(completedHistory.Select(s => s.StartTime), today);
        result.LongestStreak = StudyMetrics.CalcLongestStreak(completedHistory.Select(s => s.StartTime));

        // Focus score: today's plan adherence, studied (same "studied" definition as
        // completedHistory) vs. planned sessions today. Meaningless with zero planned sessions.
        var focusPlanned = result.TodaySessions.Count;
        var focusStudied = result.TodaySessions.Count(s => StudyMetrics.IsStudied(s, now));
        result.FocusScore = new DashboardFocusScoreDto
        {
            Planned = focusPlanned,
            Studied = focusStudied,
            Visible = focusPlanned > 0,
            Percent = focusPlanned > 0 ? Math.Min(100.0, focusStudied / (double)focusPlanned * 100) : 0,
        };

        // Inactivity nudge deliberately UNSCOPED: it mirrors the server-side
        // InactivityReminderService, which is programme-agnostic - "have you studied at all?"
        // shouldn't fire just because the last studying happened for a different programme.
        var lastPastSession = historyAllPrograms.Where(s => s.StartTime <= now).OrderByDescending(s => s.StartTime).FirstOrDefault();
        var inactivityThreshold = settings.InactivityThresholdDays > 0 ? settings.InactivityThresholdDays : 5;
        result.Inactivity = lastPastSession == null
            ? new DashboardInactivityDto { DaysSinceLastSession = inactivityThreshold, Show = true }
            : new DashboardInactivityDto
            {
                DaysSinceLastSession = (today - lastPastSession.StartTime.Date).Days,
                Show = (today - lastPastSession.StartTime.Date).Days > inactivityThreshold,
            };

        // Backup staleness hint (manual offsite download): owner-only, suppressed on demo
        // instances (the demo user IS the owner and never has a backup timestamp) and wherever
        // the raw download is unsupported, since it would nag toward an action that structurally
        // cannot succeed there.
        if (settings.LastBackupDownloadAt == null)
        {
            result.BackupHint = new DashboardBackupHintDto
            {
                NeverDownloaded = true,
                DaysSinceLastBackup = 0,
                Show = input.IsOwner && !input.IsDemo && input.RawBackupSupported,
            };
        }
        else
        {
            var daysSinceLastBackup = (today - settings.LastBackupDownloadAt.Value.Date).Days;
            result.BackupHint = new DashboardBackupHintDto
            {
                NeverDownloaded = false,
                DaysSinceLastBackup = daysSinceLastBackup,
                Show = input.IsOwner && !input.IsDemo && input.RawBackupSupported && daysSinceLastBackup > BackupStalenessThresholdDays,
            };
        }

        // Weekly quota (configurable target, default 25-30 h/week). Deliberately the PLANNED
        // week - unlike every other hours figure on this page, this tile answers "how much is
        // scheduled/done this week against the goal?", so a session already in the calendar for
        // tonight counts toward it.
        var weekMin = settings.WeeklyGoalMinHours;
        var weekMax = settings.WeeklyGoalMaxHours;
        var weekHoursVal = weekSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var weekQuota = StudyMetrics.CalcQuota(weekHoursVal, weekMin, weekMax);
        result.WeekQuota = new DashboardQuotaTileDto
        {
            TargetMin = weekMin,
            TargetMax = weekMax,
            Percent = weekQuota.Percent,
            MinPercent = weekQuota.MinPercent,
            Warning = weekQuota.Warning,
            HoursLabel = StudyMetrics.FormatHoursMinutes(weekHoursVal, omitZeroMinutes: true),
            MissingLabel = weekQuota.Warning ? StudyMetrics.FormatHoursMinutes(weekQuota.MissingHours, omitZeroMinutes: true) : "",
        };

        // Monthly quota: absolute monthly goal, independently configurable from the weekly goal.
        // Deliberately NOT prorated - the full goal applies to the label, the bar and the warning
        // alike, so early-month progress simply shows as a small fill against the whole month.
        var monthStart = new DateTime(today.Year, today.Month, 1);
        // Planned month, for the same reason as the weekly quota above.
        var monthSessions = history.Where(s => s.StartTime.Date >= monthStart).ToList();
        var monthHoursVal = monthSessions.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var monthQuota = StudyMetrics.CalcQuota(monthHoursVal, settings.MonthlyGoalMinHours, settings.MonthlyGoalMaxHours);
        result.MonthQuota = new DashboardQuotaTileDto
        {
            TargetMin = settings.MonthlyGoalMinHours,
            TargetMax = settings.MonthlyGoalMaxHours,
            Percent = monthQuota.Percent,
            MinPercent = monthQuota.MinPercent,
            Warning = monthQuota.Warning,
            HoursLabel = StudyMetrics.FormatHoursMinutes(monthHoursVal, omitZeroMinutes: true),
            MissingLabel = monthQuota.Warning ? StudyMetrics.FormatHoursMinutes(monthQuota.MissingHours, omitZeroMinutes: true) : "",
        };

        // Weekly trend (last 8 weeks) - studied hours, same semantics as the week stats card, so
        // the current bar never counts a session that is only scheduled for later this week.
        var trendHours = new double[TrendWeeks];
        var trendStarts = new DateTime[TrendWeeks];
        for (var i = TrendWeeks - 1; i >= 0; i--)
        {
            var wStart = weekStart.AddDays(-7 * i);
            var idx = TrendWeeks - 1 - i;
            trendStarts[idx] = wStart;
            trendHours[idx] = completedHistory
                .Where(s => s.StartTime.Date >= wStart && s.StartTime.Date < wStart.AddDays(7))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        }
        var maxTrendHours = Math.Max(1, trendHours.Max());
        result.WeeklyTrend = Enumerable.Range(0, TrendWeeks)
            .Select(i => new DashboardTrendWeekDto
            {
                Label = trendStarts[i].ToString("dd.MM"),
                Hours = trendHours[i],
                Percent = Math.Min(100, trendHours[i] / maxTrendHours * 100),
                IsCurrent = trendStarts[i] == weekStart,
            })
            .ToList();

        // Week-over-week delta, strictly like-for-like: the previous week counts only the same
        // stretch of weekdays that has elapsed in the current one (shared PartialWeekHours, the
        // approach BuildAnomalyHint already used). Against a FULL previous week, a week in
        // progress lost every Monday morning purely because it had not happened yet.
        var daysElapsed = DaysElapsedInWeek(weekStart, today);
        var lastWeekHoursVal = PartialWeekHours(completedHistory, weekStart.AddDays(-7), daysElapsed);
        var deltaHours = weekStudiedHours - lastWeekHoursVal;
        result.WeekDeltaUp = deltaHours >= 0;
        result.WeekDeltaLabel = StudyMetrics.FormatHoursMinutes(Math.Abs(deltaHours), omitZeroMinutes: true);

        // Recently completed sessions (quick "what did I last study" recall)
        result.RecentSessions = completedHistory
            .OrderByDescending(s => s.StartTime)
            .Take(5)
            .ToList();

        // Today's ring - studied hours only, like the week stats card: a ring that already filled
        // itself from tonight's planned session claimed the day was done before it started.
        // Daily target derived from the weekly quota (÷ 7).
        var todayHoursVal = result.TodaySessions
            .Where(s => StudyMetrics.IsStudied(s, now))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var dailyTargetHours = (weekMin + weekMax) / 2.0 / 7.0;
        var todayRingPercentRaw = todayHoursVal / dailyTargetHours * 100;
        result.TodayRing = new DashboardTodayRingDto
        {
            RingPercent = Math.Min(100, todayRingPercentRaw),
            Exceeded = todayRingPercentRaw >= 100,
            HoursLabel = StudyMetrics.FormatHoursMinutes(todayHoursVal, omitZeroMinutes: true),
            DailyTargetLabel = $"{dailyTargetHours.ToString("0.#", CultureInfo.InvariantCulture)}h",
        };

        // 7-day streak strip (studied = at least one completed session that day)
        result.StreakStrip = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(day => new DashboardDayDotDto
            {
                Label = day.ToString("ddd", CultureInfo.InvariantCulture),
                Studied = completedHistory.Any(s => s.StartTime.Date == day),
                IsToday = day == today,
            })
            .ToList();

        result.MiniDonut = BuildMiniDonut(completedHistory, allCourses, today);
        result.NeglectedCourse = BuildNeglectedCourse(settings, allCourses, completedHistory, today);
        result.ProductivityHint = BuildProductivityHint(completedHistory, settings, result.TodaySessions, now, today);
        result.WeekdayInsight = BuildWeekdayInsight(completedHistory);
        result.AnomalyHint = BuildAnomalyHint(completedHistory, today);
        result.LatestNote = BuildLatestNote(input.Notes, allCourses);
        return result;
    }

    /// <summary>Weekdays of the current week that have already happened: 1 (Monday) .. 7 (Sunday).</summary>
    private static int DaysElapsedInWeek(DateTime weekStart, DateTime today) => (today - weekStart).Days + 1;

    /// <summary>
    /// Studied hours over the first <paramref name="daysElapsed"/> days of the week starting at
    /// <paramref name="weekStart"/> - the like-for-like building block behind both the
    /// week-over-week delta and the anomaly baseline: a week in progress may only ever be
    /// compared against the same stretch of an earlier week.
    /// </summary>
    private static double PartialWeekHours(List<StudySessionDto> studiedHistory, DateTime weekStart, int daysElapsed) =>
        studiedHistory
            .Where(s => s.StartTime.Date >= weekStart && s.StartTime.Date < weekStart.AddDays(daysElapsed))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

    /// <summary>"3h" / "3h 20m" - the label shape shared by the month comparison and the
    /// best-record card, minutes omitted when zero.</summary>
    private static string FormatHoursLabel(double hours) =>
        StudyMetrics.FormatHoursMinutes(hours, omitZeroMinutes: true);

    private static DashboardMiniDonutDto BuildMiniDonut(
        List<StudySessionDto> completedHistory, List<CourseDto> allCourses, DateTime today)
    {
        var cutoff = today.AddDays(-MiniDonutDays);
        var byCourse = completedHistory
            .Where(s => s.StartTime.Date >= cutoff)
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .Where(x => x.Hours > 0)
            .OrderByDescending(x => x.Hours)
            .ToList();

        var total = byCourse.Sum(x => x.Hours);
        var donut = new DashboardMiniDonutDto { TotalHours = total };
        if (total <= 0) return donut;

        var top = byCourse.Take(DonutMaxLegend).ToList();
        var otherHours = byCourse.Skip(DonutMaxLegend).Sum(x => x.Hours);

        var slices = new List<DashboardDonutSliceDto>();
        foreach (var (courseId, hours) in top)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == courseId);
            slices.Add(new DashboardDonutSliceDto
            {
                CourseId = courseId,
                CourseName = course?.Name,
                Color = course?.Color ?? "#888888",
                Hours = hours,
                Percent = hours / total * 100,
            });
        }
        if (otherHours > 0)
        {
            slices.Add(new DashboardDonutSliceDto
            {
                CourseId = 0,
                CourseName = null,
                Color = "#7a7a8c",
                Hours = otherHours,
                Percent = otherHours / total * 100,
                IsOther = true,
            });
        }
        donut.Slices = slices;

        var parts = new List<string>();
        var cursor = 0.0;
        foreach (var s in slices)
        {
            var start = cursor;
            var end = cursor + s.Percent;
            parts.Add($"{s.Color} {start.ToString("0.###", CultureInfo.InvariantCulture)}% {end.ToString("0.###", CultureInfo.InvariantCulture)}%");
            cursor = end;
        }
        donut.Gradient = "conic-gradient(" + string.Join(", ", parts) + ")";
        return donut;
    }

    private static DashboardNeglectedCourseDto? BuildNeglectedCourse(
        UserSettingsDto settings, List<CourseDto> allCourses, List<StudySessionDto> completedHistory, DateTime today)
    {
        var pick = StudyMetrics.CalcNeglectedCourse(
            allCourses, settings.SelectedCourseIds, settings.CompletedCourseIds, completedHistory, today);
        if (pick == null) return null;

        return new DashboardNeglectedCourseDto
        {
            CourseId = pick.Value.Course.Id,
            Name = pick.Value.Course.Name,
            Icon = pick.Value.Course.Icon,
            Color = pick.Value.Course.Color,
            DaysSinceLastStudied = pick.Value.LastStudied.HasValue
                ? (today - pick.Value.LastStudied.Value.Date).Days
                : null,
        };
    }

    /// <summary>
    /// Sums the studied hours per time-of-day bucket. Four states: (a) something is already
    /// planned today in the best bucket -> confirm, (b) nothing planned yet and the bucket window
    /// (intersected with the study window) isn't entirely in the past yet today -> suggest a plan
    /// with a calendar link, (c) window over or not a study day per StudyDays -> just the neutral
    /// insight, (d) too little data or no clear pattern -> hide the card entirely.
    /// </summary>
    private static DashboardProductivityHintDto BuildProductivityHint(
        List<StudySessionDto> studiedHistory, UserSettingsDto settings,
        List<StudySessionDto> todaySessions, DateTime now, DateTime today)
    {
        var hint = new DashboardProductivityHintDto();
        if (studiedHistory.Count < ProductivityMinSessions) return hint;

        var buckets = TimeOfDayBuckets;
        var hoursByBucket = new double[buckets.Length];
        foreach (var s in studiedHistory)
        {
            var hour = s.StartTime.Hour;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (hour >= buckets[i].From && hour < buckets[i].To)
                {
                    hoursByBucket[i] += (s.EndTime - s.StartTime).TotalHours;
                    break;
                }
            }
        }

        var total = hoursByBucket.Sum();
        if (total <= 0) return hint;
        var bestIdx = 0;
        for (var i = 1; i < hoursByBucket.Length; i++)
            if (hoursByBucket[i] > hoursByBucket[bestIdx]) bestIdx = i;
        if (hoursByBucket[bestIdx] / total < ProductivityMinShare) return hint;

        hint.Visible = true;
        hint.BestBucketIndex = bestIdx;

        var bucketStart = today.AddHours(buckets[bestIdx].From);
        var bucketEnd = today.AddHours(buckets[bestIdx].To);

        // (a) A session already overlaps the best time window today -> confirm. Deliberately
        // before the StudyDays check: confirming a session that's actually planned is correct
        // even on a non-study day; only a plan SUGGESTION would be out of place there.
        var plannedSession = todaySessions
            .Where(s => s.StartTime < bucketEnd && s.EndTime > bucketStart)
            .OrderBy(s => s.StartTime)
            .FirstOrDefault();
        if (plannedSession != null)
        {
            hint.Planned = true;
            hint.PlannedStartTimeLabel = plannedSession.StartTime.ToString("HH:mm");
            return hint;
        }

        // (c) No configured study day -> neutral insight without a call to action.
        if (!StudyPlanner.ParseStudyDays(settings.StudyDays).Contains(today.DayOfWeek)) return hint;

        // (b)/(c): Only suggest if the bucket intersected with the study window isn't entirely
        // over yet today (and the study window even touches the bucket at all).
        var effectiveStart = Math.Max(buckets[bestIdx].From, settings.StudyWindowStartHour);
        var effectiveEnd = Math.Min(buckets[bestIdx].To, settings.StudyWindowEndHour);
        if (effectiveEnd <= effectiveStart || now >= today.AddHours(effectiveEnd)) return hint;

        hint.ShowSuggestText = true;
        hint.ShowPlanLink = true;
        return hint;
    }

    /// <summary>Which weekday has the most studied hours? Purely informational, without the
    /// plan-suggestion/confirmation logic above.</summary>
    private static DashboardWeekdayInsightDto BuildWeekdayInsight(List<StudySessionDto> studiedHistory)
    {
        var insight = new DashboardWeekdayInsightDto();
        if (studiedHistory.Count < ProductivityMinSessions) return insight;

        var hoursByWeekday = new double[7]; // 0=Monday .. 6=Sunday
        foreach (var s in studiedHistory)
        {
            var idx = ((int)s.StartTime.DayOfWeek + 6) % 7;
            hoursByWeekday[idx] += (s.EndTime - s.StartTime).TotalHours;
        }

        var total = hoursByWeekday.Sum();
        if (total <= 0) return insight;
        var bestIdx = 0;
        for (var i = 1; i < hoursByWeekday.Length; i++)
            if (hoursByWeekday[i] > hoursByWeekday[bestIdx]) bestIdx = i;
        if (hoursByWeekday[bestIdx] / total < ProductivityMinShare) return insight;

        insight.Available = true;
        insight.BestIndex = bestIdx;
        return insight;
    }

    /// <summary>
    /// Compares the hours studied so far THIS week with the average of the last
    /// AnomalyBaselineWeeks previous weeks - strictly like-for-like: both sides count only the
    /// first `daysElapsed` weekdays (Monday..today), otherwise a week that just started would
    /// always lose against full previous weeks. Two safeguards against false alarms:
    /// (1) enough previous weeks must actually come from recorded history, (2) the baseline
    /// average itself must exceed AnomalyMinBaselineHours.
    /// </summary>
    private static DashboardAnomalyHintDto BuildAnomalyHint(List<StudySessionDto> completedHistory, DateTime today)
    {
        var anomaly = new DashboardAnomalyHintDto();

        var weekStart = StudyMetrics.WeekStartOf(today);
        var daysElapsed = DaysElapsedInWeek(weekStart, today);
        if (daysElapsed < AnomalyMinDaysElapsed) return anomaly;

        if (completedHistory.Count == 0) return anomaly;
        var earliestWeekStart = StudyMetrics.WeekStartOf(completedHistory.Min(s => s.StartTime.Date));

        var baselineHours = new List<double>();
        for (var i = 1; i <= AnomalyBaselineWeeks; i++)
        {
            var wStart = weekStart.AddDays(-7 * i);
            if (wStart < earliestWeekStart) break; // older weeks lie before the start of recording
            baselineHours.Add(PartialWeekHours(completedHistory, wStart, daysElapsed));
        }
        if (baselineHours.Count < AnomalyMinBaselineWeeks) return anomaly;

        var baselineAvg = baselineHours.Average();
        if (baselineAvg < AnomalyMinBaselineHours) return anomaly;

        var currentHours = PartialWeekHours(completedHistory, weekStart, daysElapsed);
        var ratio = currentHours / baselineAvg;
        if (ratio >= AnomalyThresholdRatio) return anomaly;

        anomaly.Show = true;
        anomaly.PercentVsBaseline = (int)Math.Round(ratio * 100);
        return anomaly;
    }

    /// <summary>
    /// Active-programme scope: general notes (no CourseId) always stay visible, course-bound ones
    /// only if the course belongs to the active programme. NotesCount reuses the same list
    /// instead of firing a second request just for the count.
    /// </summary>
    private static DashboardLatestNoteDto BuildLatestNote(List<NoteDto> allNotes, List<CourseDto> allCourses)
    {
        var allowedCourseIds = allCourses.Select(c => c.Id).ToHashSet();
        var notes = allNotes
            .Where(n => !n.CourseId.HasValue || allowedCourseIds.Contains(n.CourseId.Value))
            .ToList();

        var result = new DashboardLatestNoteDto { NotesCount = notes.Count };
        var latest = notes.FirstOrDefault(); // server sorts by UpdatedAt descending
        if (latest == null) return result;

        result.Note = latest;
        result.Excerpt = latest.Content.Length > 120
            ? latest.Content[..120].TrimEnd() + "…"
            : latest.Content;
        result.CourseName = latest.CourseId.HasValue
            ? allCourses.FirstOrDefault(c => c.Id == latest.CourseId)?.Name
            : null;
        return result;
    }

    // ── Phase 3: goals / programmes / quotas ──────────────────────────────────

    /// <summary>
    /// Phase 3: goals, ECTS/average grade, topic progress. Active-programme scope: goals/grades
    /// from other programmes must not factor into either the average grade or the upcoming
    /// deadlines.
    /// </summary>
    public static DashboardGoalsSummaryDto BuildGoals(DashboardSummaryInput input)
    {
        var settings = input.Settings;
        var allCourses = input.AllCourses;
        var activeCourseIds = ActiveCourseIds(input);
        var today = input.Today;

        var goals = input.Goals.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();

        var result = new DashboardGoalsSummaryDto
        {
            CourseTags = goals.ToDictionary(g => g.CourseId, g => g.Tag),
            CourseDeadlineDays = goals
                .Where(g => g.TargetDate.HasValue && g.CompletedAt == null)
                .Select(g => new { g.CourseId, Days = (g.TargetDate!.Value.Date - today).Days })
                .Where(x => x.Days <= CourseDeadlineCutoffDays)
                .ToDictionary(x => x.CourseId, x => x.Days),
            UpcomingGoals = StudyMetrics.CalcUpcomingCourseGoals(goals, today)
                .Select(g => new DashboardUpcomingGoalDto
                {
                    CourseId = g.CourseId,
                    CourseName = g.CourseName,
                    TargetDate = g.TargetDate,
                    DaysLeft = g.DaysLeft,
                })
                .ToList(),
        };

        // ECTS & average grade (same Ects-weighting as the analytics page). Programme-aware: the
        // group quotas of the ACTIVE programme.
        result.EctsTotal = CourseCatalog.CalcTotalEcts(allCourses, input.GroupQuotas);
        result.EctsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, input.GroupQuotas);
        result.EctsPercent = result.EctsTotal > 0 ? Math.Min(100.0, result.EctsEarned / (double)result.EctsTotal * 100) : 0;

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        result.AverageGradeLabel = averageGrade.HasValue ? StudyMetrics.FormatGrade(averageGrade.Value) : "–";

        var topics = StudyMetrics.CalcTopicsProgress(allCourses, settings.SelectedCourseIds, goals);
        result.TopicsCompleted = topics.Completed;
        result.TopicsTotal = topics.Total;
        result.TopicsPercent = topics.Percent;

        // Programmes-completed achievement: counts across ALL of the user's programmes
        // (deliberately NOT scoped - "how many programmes have you completed in total" is by
        // definition a cross-programme milestone). IsCompleted is a purely manual flag; the
        // built-in programme never counts (no DB entry).
        result.ProgramsCompleted = input.StudyPrograms.Count(p => p.IsCompleted);
        return result;
    }

    // ── Phase 5: all-time history ─────────────────────────────────────────────

    /// <summary>
    /// Phase 5: forecast, graduation goal, month comparison, best records and achievements - all
    /// from the all-time history. Depends on the ECTS numbers from
    /// <see cref="BuildGoals"/> and the note count from <see cref="BuildSessions"/>, exactly as
    /// the page's phase order does.
    /// </summary>
    public static DashboardProgressSummaryDto BuildProgress(
        DashboardSummaryInput input, DashboardSessionsSummaryDto sessions, DashboardGoalsSummaryDto goals)
    {
        var settings = input.Settings;
        var activeCourseIds = ActiveCourseIds(input);
        // Scoped to the active programme on every build: the filtering is cheap and in-memory,
        // only the ~10-year fetch that produced HeavyHistory is expensive.
        var allTimeHistory = (input.HeavyHistory ?? new List<StudySessionDto>())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        var forecast = StudyMetrics.CalcForecast(goals.EctsTotal, goals.EctsEarned, input.AllCourses,
            settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, allTimeHistory, input.Now);

        return new DashboardProgressSummaryDto
        {
            Forecast = new DashboardForecastDto
            {
                Available = forecast.Available,
                AlreadyDone = forecast.AlreadyDone,
                DateLabel = forecast.Available ? forecast.ForecastDate!.Value.ToString("dd.MM.yyyy") : "",
            },
            GraduationGoal = BuildGraduationGoal(input, forecast),
            MonthComparison = BuildMonthComparison(allTimeHistory, input.Today),
            BestRecords = BuildBestRecords(allTimeHistory, input.Today),
            Achievements = BuildAchievements(settings, activeCourseIds, allTimeHistory, goals, sessions.LatestNote.NotesCount),
        };
    }

    /// <summary>
    /// Desired graduation date: the inverse of the forecast - "how many h/week do I need to be
    /// done by the target date?". Shares every guard with the forecast (no target date,
    /// everything completed, or missing semester structure -> hide the card entirely). From the
    /// same semester baseline model, the structurally still-needed total effort follows; spread
    /// across the weeks until the target date, that's the required pace. "On track" = the same
    /// 8-week pace that also refines the forecast.
    /// </summary>
    private static DashboardGraduationGoalDto BuildGraduationGoal(
        DashboardSummaryInput input, StudyMetrics.ForecastResult forecast)
    {
        var goal = new DashboardGraduationGoalDto();
        if (!forecast.Available || !input.Settings.TargetGraduationDate.HasValue) return goal;

        goal.Visible = true;
        var targetDate = input.Settings.TargetGraduationDate.Value.Date;
        goal.TargetDateValue = targetDate.ToString("dd.MM.yyyy");
        var weeksUntilTarget = (targetDate - input.Today).TotalDays / 7.0;
        goal.Expired = weeksUntilTarget <= 0;
        if (goal.Expired) return goal;

        var remainingEffortHours = forecast.BaselineWeeksNeeded * forecast.ReferenceWeeklyHours;
        var requiredWeeklyHours = remainingEffortHours / weeksUntilTarget;
        goal.OnTrack = forecast.RecentWeeklyHours >= requiredWeeklyHours;
        goal.RequiredValue = requiredWeeklyHours.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        goal.PaceValue = forecast.RecentWeeklyHours.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return goal;
    }

    private static DashboardMonthComparisonDto BuildMonthComparison(List<StudySessionDto> allTimeHistory, DateTime today)
    {
        var result = StudyMetrics.CalcMonthComparison(allTimeHistory, today);
        var dto = new DashboardMonthComparisonDto
        {
            CurrentLabel = FormatHoursLabel(result.CurrentMonthHours),
            VsLastMonthUp = result.DeltaVsPreviousMonth >= 0,
            VsLastMonthLabel = FormatHoursLabel(Math.Abs(result.DeltaVsPreviousMonth)),
            HasYearData = result.HasYearData,
        };
        if (result.HasYearData)
        {
            dto.VsLastYearUp = result.DeltaVsLastYear!.Value >= 0;
            dto.VsLastYearLabel = FormatHoursLabel(Math.Abs(result.DeltaVsLastYear!.Value));
        }
        return dto;
    }

    /// <summary>
    /// Single all-time best value for a day or a week (Mon-Sun). "New record" when today or the
    /// current week already reaches/exceeds the previous best - since the history extends to
    /// "now", the best value in that case is simply today/this week itself.
    /// </summary>
    private static DashboardBestRecordsDto BuildBestRecords(List<StudySessionDto> allTimeHistory, DateTime today)
    {
        if (allTimeHistory.Count == 0)
        {
            return new DashboardBestRecordsDto
            {
                BestDayHoursLabel = FormatHoursLabel(0),
                BestDayDateLabel = "–",
                BestWeekHoursLabel = FormatHoursLabel(0),
                BestWeekRangeLabel = "–",
            };
        }

        var bestDay = allTimeHistory
            .GroupBy(s => s.StartTime.Date)
            .Select(g => (Day: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .First();

        var bestWeek = allTimeHistory
            .GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime))
            .Select(g => (WeekStart: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .First();

        var todayHours = allTimeHistory.Where(s => s.StartTime.Date == today).Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var thisWeekStart = StudyMetrics.WeekStartOf(today);
        var thisWeekHours = allTimeHistory.Where(s => StudyMetrics.WeekStartOf(s.StartTime) == thisWeekStart).Sum(s => (s.EndTime - s.StartTime).TotalHours);

        return new DashboardBestRecordsDto
        {
            BestDayHoursLabel = FormatHoursLabel(bestDay.Hours),
            BestDayDateLabel = bestDay.Day.ToString("dd.MM.yyyy"),
            BestDayIsNew = todayHours > 0 && todayHours >= bestDay.Hours,
            BestWeekHoursLabel = FormatHoursLabel(bestWeek.Hours),
            BestWeekRangeLabel = $"{bestWeek.WeekStart:dd.MM.} – {bestWeek.WeekStart.AddDays(6):dd.MM.yyyy}",
            BestWeekIsNew = thisWeekHours > 0 && thisWeekHours >= bestWeek.Hours,
        };
    }

    /// <summary>
    /// Achievement tiers in render order. Thresholds/unlock computation and the raw per-category
    /// counts come from the shared AchievementCatalog, so this stays in sync with the metrics API
    /// and the server's push notifier automatically. CompletedCourseIds spans EVERY programme the
    /// user has ever created, which is why AchievementCatalog.BuildInputs intersects it with the
    /// active id set explicitly.
    /// </summary>
    private static DashboardAchievementsDto BuildAchievements(
        UserSettingsDto settings, HashSet<int> activeCourseIds, List<StudySessionDto> allTimeHistory,
        DashboardGoalsSummaryDto goals, int notesCount)
    {
        var inputs = AchievementCatalog.BuildInputs(
            allTimeHistory, settings.CompletedCourseIds, activeCourseIds,
            settings.WeeklyGoalMinHours, goals.EctsTotal, goals.EctsEarned, notesCount, goals.ProgramsCompleted);

        var tiers = new List<DashboardAchievementTierDto>();
        AddTiers(tiers, AchievementCatalog.HoursKey, AchievementCatalog.HoursTiers, inputs.TotalHours);
        AddTiers(tiers, AchievementCatalog.StreakKey, AchievementCatalog.StreakTiers, inputs.LongestStreak);
        AddTiers(tiers, AchievementCatalog.SessionsKey, AchievementCatalog.SessionsTiers, inputs.TotalSessions);
        AddTiers(tiers, AchievementCatalog.CoursesKey, AchievementCatalog.CoursesTiers, inputs.CoursesCompleted);
        tiers.Add(new DashboardAchievementTierDto
        {
            Category = AchievementCatalog.AllCoursesKey,
            Threshold = 1,
            Unlocked = inputs.AllCoursesDone,
            Current = inputs.AllCoursesDone ? 1 : 0,
        });
        AddTiers(tiers, AchievementCatalog.EarlyBirdKey, AchievementCatalog.EarlyBirdTiers, inputs.EarlyBirdCount);
        AddTiers(tiers, AchievementCatalog.NightOwlKey, AchievementCatalog.NightOwlTiers, inputs.NightOwlCount);
        AddTiers(tiers, AchievementCatalog.WeekendKey, AchievementCatalog.WeekendTiers, inputs.WeekendCount);
        AddTiers(tiers, AchievementCatalog.MarathonKey, AchievementCatalog.MarathonTiers, inputs.LongestSessionHours);
        AddTiers(tiers, AchievementCatalog.PerfectWeekKey, AchievementCatalog.PerfectWeekTiers, inputs.PerfectWeeks);
        AddTiers(tiers, AchievementCatalog.NotesKey, AchievementCatalog.NotesTiers, inputs.NotesCount);
        AddTiers(tiers, AchievementCatalog.CourseDiversityKey, AchievementCatalog.CourseDiversityTiers, inputs.MaxCourseDiversity);
        AddTiers(tiers, AchievementCatalog.ProgramsKey, AchievementCatalog.ProgramsTiers, inputs.ProgramsCompleted);

        return new DashboardAchievementsDto
        {
            Unlocked = tiers.Count(t => t.Unlocked),
            Total = tiers.Count,
            Tiers = tiers,
        };
    }

    private static void AddTiers(List<DashboardAchievementTierDto> tiers, string category, int[] thresholds, double current)
    {
        foreach (var tier in AchievementCatalog.BuildTiers(thresholds, current))
            tiers.Add(new DashboardAchievementTierDto
            {
                Category = category,
                Threshold = tier.Threshold,
                Unlocked = tier.Unlocked,
                Current = tier.Current,
            });
    }
}
