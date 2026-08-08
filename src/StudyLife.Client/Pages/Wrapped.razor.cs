using System.Net.Http.Json;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Wrapped
{
    private I18nText.WrappedText T = new();

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
    // side dominates" is enough for a single recap tile here.
    private string ChronotypeText => _earlyBirdHours > _nightOwlHours
        ? T.EarlyBirdText ?? ""
        : _nightOwlHours > _earlyBirdHours
            ? T.NightOwlText ?? ""
            : T.BalancedChronotypeText ?? "";

    protected override async Task OnInitializedAsync()
    {
        T = await I18nText.GetTextTableAsync<I18nText.WrappedText>(this);
        _langWatcher = new I18nLanguageWatcher(I18nText);
        await _langWatcher.InitAsync();

        var settings = await State.GetSettingsAsync();
        var allCourses = await State.GetCoursesAsync();
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();

        _periodEnd = DateTime.Today;
        _periodStart = _periodEnd.AddDays(-365);

        // Recap period (365 days, default of /api/sessions/history) - for total hours,
        // sessions, streak, top course, weekday, and chronotype highlight.
        var periodHistory = (await State.GetJsonCachedAsync<List<StudySessionDto>>("api/sessions/history") ?? new())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        // All-time history for the achievement count - the same ~10-year span as
        // Index.Achievements.razor.cs' AchievementHistoryDays, since achievements are
        // deliberately programme-wide milestones, not tied to the recap period.
        var allTimeHistory = (await State.GetJsonCachedAsync<List<StudySessionDto>>("api/sessions/history?days=3650") ?? new())
            .Where(s => activeCourseIds.Contains(s.CourseId))
            .ToList();

        _totalHours = periodHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _totalHoursLabel = FormatHours(_totalHours);
        _totalSessions = periodHistory.Count;
        _longestStreak = StudyMetrics.CalcLongestStreak(periodHistory.Select(s => s.StartTime));

        var byCourse = periodHistory
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .FirstOrDefault();
        if (byCourse.Hours > 0)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == byCourse.CourseId);
            var sample = periodHistory.First(s => s.CourseId == byCourse.CourseId);
            _topCourse = new TopCourseInfo(
                course?.Name ?? sample.CourseName,
                course?.Icon ?? "📚",
                course?.Color ?? sample.CourseColor,
                byCourse.Hours);
        }

        var hoursByWeekday = new double[7];
        foreach (var s in periodHistory)
            hoursByWeekday[((int)s.StartTime.DayOfWeek + 6) % 7] += (s.EndTime - s.StartTime).TotalHours;
        var bestWeekdayIdx = 0;
        for (var i = 1; i < 7; i++)
            if (hoursByWeekday[i] > hoursByWeekday[bestWeekdayIdx]) bestWeekdayIdx = i;
        if (hoursByWeekday[bestWeekdayIdx] > 0)
        {
            _busiestWeekdayIdx = bestWeekdayIdx;
            _busiestWeekday = new WeekdayInfo(WeekdayName(bestWeekdayIdx), hoursByWeekday[bestWeekdayIdx]);
        }

        _earlyBirdHours = periodHistory.Where(s => s.StartTime.Hour < 7).Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _nightOwlHours = periodHistory.Where(s => s.StartTime.Hour >= 22).Sum(s => (s.EndTime - s.StartTime).TotalHours);

        await BuildAchievementCountAsync(settings, activeCourseIds, allCourses, allTimeHistory);
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

        // _langWatcher can still be null here: OnInitializedAsync's first await doesn't complete
        // synchronously, so Blazor renders once - firstRender=true - before the rest of
        // OnInitializedAsync (incl. _langWatcher's assignment) has run; a later render catches up.
        if (_langWatcher != null && await _langWatcher.CheckChangedAsync() && _busiestWeekday != null && _busiestWeekdayIdx >= 0)
        {
            _busiestWeekday = _busiestWeekday with { Label = WeekdayName(_busiestWeekdayIdx) };
            await InvokeAsync(StateHasChanged);
        }
    }

    // Mirrors the inputs that Index.Achievements.razor.cs' BuildAchievements uses for the same 13
    // categories - see StudyMetrics.CountUnlockedAchievements for the actual
    // thresholds. Deliberately all-time (allTimeHistory), not limited to the recap
    // period: achievements are programme-wide milestones, not a period comparison - a
    // "before/after" split couldn't be cleanly derived for categories without a date
    // reference (e.g. programmes completed; IsCompleted is a plain flag without a timestamp).
    private async Task BuildAchievementCountAsync(
        UserSettings settings, HashSet<int> activeCourseIds, List<CourseDto> allCourses, List<StudySessionDto> allTimeHistory)
    {
        var totalHours = allTimeHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = allTimeHistory.Count;
        var longestStreak = StudyMetrics.CalcLongestStreak(allTimeHistory.Select(s => s.StartTime));
        var coursesCompleted = settings.CompletedCourseIds.Count(id => activeCourseIds.Contains(id));

        var groupQuotas = await State.GetActiveGroupQuotasAsync();
        var ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, groupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, groupQuotas);
        var allCoursesDone = ectsTotal > 0 && ectsEarned >= ectsTotal;

        var earlyBirdCount = allTimeHistory.Count(s => s.StartTime.Hour < 7);
        var nightOwlCount = allTimeHistory.Count(s => s.StartTime.Hour >= 22);
        var weekendCount = allTimeHistory.Count(s => s.StartTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var longestSessionHours = allTimeHistory.Count > 0 ? allTimeHistory.Max(s => (s.EndTime - s.StartTime).TotalHours) : 0;

        var weeklyGroups = allTimeHistory.GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime)).ToList();
        var perfectWeeks = settings.WeeklyGoalMinHours > 0
            ? weeklyGroups.Count(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours) >= settings.WeeklyGoalMinHours)
            : 0;
        var maxCourseDiversity = weeklyGroups.Count > 0
            ? weeklyGroups.Max(g => g.Select(s => s.CourseId).Distinct().Count())
            : 0;

        var notes = await State.GetJsonCachedAsync<List<NoteDto>>("api/notes") ?? new();
        var notesCount = notes.Count(n => !n.CourseId.HasValue || activeCourseIds.Contains(n.CourseId.Value));

        var studyPrograms = await State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms") ?? new();
        var programsCompleted = studyPrograms.Count(p => p.IsCompleted);

        (_achievementsUnlocked, _achievementsTotal) = StudyMetrics.CountUnlockedAchievements(
            totalHours, longestStreak, totalSessions, coursesCompleted, allCoursesDone,
            earlyBirdCount, nightOwlCount, weekendCount, longestSessionHours,
            perfectWeeks, notesCount, maxCourseDiversity, programsCompleted);
    }

    private static string FormatHours(double hours) => $"{(int)hours}h {(int)((hours - (int)hours) * 60)}m";
}
