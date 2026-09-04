using System.Net.Http.Json;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Wrapped
{
    // Recap period: rolling 365 days (default window of GET api/sessions/history,
    // days=365), deliberately instead of a "study year"/semester concept - the data model has no
    // clean academic year (StudyProgramEntity has no start date), and a rolling
    // window is always available, even right after onboarding.
    private DateTime _periodStart;
    private DateTime _periodEnd;

    private double _totalHours;
    private string _totalHoursLabel = "0h 0m";
    private int _totalSessions;
    private int _longestStreak;

    private sealed record TopCourseInfo(string Name, string Icon, string Color, double Hours);
    private TopCourseInfo? _topCourse;

    private sealed record WeekdayInfo(string Label, double Hours);
    private WeekdayInfo? _busiestWeekday;
    private int _busiestWeekdayIdx = -1;

    private I18nLanguageWatcher _langWatcher = null!;

    // Chronotype highlight: compares hours before 7am ("early bird") with hours from 10pm
    // ("night owl") in the recap period - the same hour boundaries as the achievement categories
    // AchievementEarlyBirdName/AchievementNightOwlName in Index.Achievements.razor.cs, just without
    // a threshold (a simple size comparison is enough for a "highlight" here).
    private double _earlyBirdHours;
    private double _nightOwlHours;

    private int _achievementsUnlocked;
    private int _achievementsTotal;

    // Rough chronotype comparison for the highlight - no threshold like the
    // early-bird/night-owl achievements (Index.Achievements.razor.cs), "which
    // side dominates" is enough for a single recap tile here. Only ever rendered from markup
    // gated by IsTextLoaded, so T is guaranteed loaded here - no defensive "?? """ needed.
    private string ChronotypeText => _earlyBirdHours > _nightOwlHours
        ? T.EarlyBirdText
        : _nightOwlHours > _earlyBirdHours
            ? T.NightOwlText
            : T.BalancedChronotypeText;

    // Independent of each other, and of the text-table fetch that LocalizedComponentBase starts
    // in parallel - kicked off here (OnInitializingAsync, runs alongside that fetch) instead of
    // await-ing one after another (same pattern as Index.razor.cs/Setup.razor).
    private Task<UserSettings>? _settingsTask;
    private Task<List<CourseDto>>? _coursesTask;
    private Task<List<StudySessionDto>?>? _periodHistoryTask;
    private Task<List<StudySessionDto>?>? _allTimeHistoryTask;

    // Progressive render (2026-09 audit): default true so the first OnTextLoadedAsync run shows a
    // skeleton for the slides each flag covers instead of their zero/empty field defaults.
    // _recapLoading gates the fixed hours/sessions/streak slides (the optional top-course/
    // busiest-weekday/chronotype slides already stay safely hidden via their own null/zero
    // checks, so they need no extra gating); _achievementsLoading gates the achievements slide,
    // whose own fetch chain (BuildAchievements) is deliberately a separate, later phase.
    private bool _recapLoading = true;
    private bool _achievementsLoading = true;

    protected override bool RenderShellBeforeData => true;

    private DateTime _now;
    private Task<WrappedSummaryDto?>? _summaryTask;

    protected override Task OnInitializingAsync()
    {
        _settingsTask = State.GetSettingsAsync();
        _coursesTask = State.GetCoursesAsync();
        // One wall-clock read shared by the server request and the local fallback, so both compute
        // the recap for the same instant. The raw history fetches only start in the fallback.
        _now = DateTime.Now;
        _summaryTask = State.TryGetSummaryAsync<WrappedSummaryDto>("api/wrapped/summary", _now);
        // Doesn't depend on any fetch - computed here (instead of after the awaits below) so the
        // period line in the header is already correct on the very first shell render.
        _periodEnd = DateTime.Today;
        _periodStart = _periodEnd.AddDays(-WrappedSummaryBuilder.PeriodHistoryDays);
        return Task.CompletedTask;
    }

    protected override async Task OnTextLoadedAsync()
    {
        _langWatcher = new I18nLanguageWatcher(I18nText);
        await _langWatcher.InitAsync();

        var settings = await _settingsTask!;
        var allCourses = await _coursesTask!;

        var summary = await _summaryTask!;
        if (summary != null)
        {
            ApplyRecap(summary.Recap);
            _recapLoading = false;
            await RenderPhaseAsync();

            _achievementsUnlocked = summary.Achievements.Unlocked;
            _achievementsTotal = summary.Achievements.Total;
            _achievementsLoading = false;
            await RenderPhaseAsync();
            return;
        }

        // Fallback (offline or a server without api/wrapped/summary): raw fetches + local builder.
        _periodHistoryTask = State.GetHistoryAsync(WrappedSummaryBuilder.PeriodHistoryDays);
        _allTimeHistoryTask = State.GetHistoryAsync(WrappedSummaryBuilder.AllTimeHistoryDays);

        // Every number below comes from WrappedSummaryBuilder (StudyLife.Shared), which the
        // server can run against the same inputs - this method only fetches, hands the raw
        // inputs over, and copies the result into the fields the markup binds to. The builder is
        // called once per phase (rather than once for everything) precisely so the phase boundary
        // survives: the achievements fetch below is a separate, much longer-range one that must
        // never hold back the recap slides.
        var input = new WrappedSummaryInput
        {
            Settings = AppStateService.ToDto(settings),
            AllCourses = allCourses,
            PeriodHistory = await _periodHistoryTask! ?? new(),
            Now = _now,
        };

        // ── Phase 1: recap period (365 days) - total hours, sessions, streak, top course,
        // weekday, and chronotype highlight.
        ApplyRecap(WrappedSummaryBuilder.BuildRecap(input));

        _recapLoading = false;
        await RenderPhaseAsync();

        // ── Phase 2: achievement count - its own, much longer-range fetch (~10 years, same span
        // as Index.Achievements.razor.cs' AchievementHistoryDays) plus notes/programmes, kept
        // separate so it never holds back the recap slides above. Independent of each other and
        // of the pure-CPU computation in BuildAchievements - started now, awaited below, same as
        // the original BuildAchievementCountAsync.
        var groupQuotasTask = State.GetActiveGroupQuotasAsync();
        var notesTask = State.GetJsonCachedAsync<List<NoteDto>>("api/notes");
        var studyProgramsTask = State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms");

        input.AllTimeHistory = await _allTimeHistoryTask! ?? new();
        input.GroupQuotas = await groupQuotasTask;
        input.Notes = await notesTask ?? new();
        input.StudyPrograms = await studyProgramsTask ?? new();

        var achievements = WrappedSummaryBuilder.BuildAchievements(input);
        _achievementsUnlocked = achievements.Unlocked;
        _achievementsTotal = achievements.Total;

        _achievementsLoading = false;
        await RenderPhaseAsync();
    }

    /// <summary>Phase 1 result -> fields. Only the localized weekday name is assembled here, from
    /// the raw index the builder returns; every number/label already comes formatted from it.</summary>
    private void ApplyRecap(WrappedRecapDto r)
    {
        _totalHours = r.TotalHours;
        _totalHoursLabel = r.TotalHoursLabel;
        _totalSessions = r.TotalSessions;
        _longestStreak = r.LongestStreak;

        _topCourse = r.TopCourse == null
            ? null
            : new TopCourseInfo(r.TopCourse.Name, r.TopCourse.Icon, r.TopCourse.Color, r.TopCourse.Hours);

        if (r.BusiestWeekday == null)
        {
            _busiestWeekday = null;
            _busiestWeekdayIdx = -1;
        }
        else
        {
            _busiestWeekdayIdx = r.BusiestWeekday.Index;
            _busiestWeekday = new WeekdayInfo(WeekdayName(r.BusiestWeekday.Index), r.BusiestWeekday.Hours);
        }

        _earlyBirdHours = r.EarlyBirdHours;
        _nightOwlHours = r.NightOwlHours;
    }

    private string WeekdayName(int idx)
    {
        string?[] names = [T.WeekdayMon, T.WeekdayTue, T.WeekdayWed, T.WeekdayThu, T.WeekdayFri, T.WeekdaySat, T.WeekdaySun];
        return names[idx] ?? "";
    }

    /// <summary>_busiestWeekday.Label was copied from T once at load time, so a live language
    /// switch (no page reload) left it stuck on whatever language was active back then - same
    /// root cause/fix shape as Focus.razor's RefreshLocalizedModeNames. The heavy history fetch/
    /// aggregation isn't redone, just the label for the already-known busiest weekday index.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) return;

        // _langWatcher can still be null here: LocalizedComponentBase's OnInitializedAsync awaits
        // the text-table fetch before this component's own OnTextLoadedAsync (where _langWatcher
        // is assigned) even starts, and that first await doesn't complete synchronously, so Blazor
        // renders once - firstRender=true - before OnTextLoadedAsync has run; a later render
        // catches up once it's set.
        if (_langWatcher != null && await _langWatcher.CheckChangedAsync() && _busiestWeekday != null && _busiestWeekdayIdx >= 0)
        {
            _busiestWeekday = _busiestWeekday with { Label = WeekdayName(_busiestWeekdayIdx) };
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string FormatHours(double hours) => $"{(int)hours}h {(int)((hours - (int)hours) * 60)}m";
}
