using System.Net.Http.Json;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // Mini course-time donut (last 30 days)
    private List<DashboardCourseDonutCard.DonutSlice> _miniDonutSlices = new();
    private string _miniDonutGradient = "";
    private double _miniDonutTotalHours;

    // Latest note preview
    private NoteDto? _latestNote;
    private string _latestNoteExcerpt = "";
    private string? _latestNoteCourseName;

    // Least-attention course (active course studied longest ago, or never)
    private DashboardNeglectedCourseCard.NeglectedCourse? _neglectedCourse;
    private string _neglectedCourseHint = "";

    // Productivity hint: translates the study rhythm (Stats' time-of-day buckets) into a concrete
    // suggestion for today - see BuildProductivityHint. Only visible with sufficient data.
    private bool _productivityHintVisible;
    private string _productivityInsight = "";
    private bool _productivityPlanned;
    private string? _productivityStatus;
    private bool _productivityShowPlanLink;
    private const int ProductivityMinSessions = 10;
    private const double ProductivityMinShare = 0.30;

    // Second, simpler insight (best weekday) that rotates with the time-of-day insight above (Task 4).
    // _insightVariant is picked once per page load (OnInitializedAsync), same idea as _motivation's
    // random pick, so the card doesn't flicker between variants on every re-render/StateHasChanged.
    // BuildProductivityHint's own (a)/(b)/(c)/(d) state machine above is left completely untouched -
    // when the weekday variant wins AND has enough data, its result simply overwrites the four
    // _productivity* fields afterwards (see LoadDataAsync); otherwise the time-of-day result stands.
    private int _insightVariant;
    private bool _weekdayInsightAvailable;
    private string _weekdayInsightText = "";
    private const int MiniDonutDays = 30;

    // Anomaly hint ("noticeably less this week than usual"): see BuildAnomalyHint.
    private bool _showAnomalyHint;
    private int _anomalyPercentVsBaseline;
    private const int AnomalyBaselineWeeks = 8;
    private const int AnomalyMinBaselineWeeks = 4;
    private const double AnomalyThresholdRatio = 0.5;
    private const double AnomalyMinBaselineHours = 1.0;
    // Before Wednesday (< 3 elapsed weekdays), even a like-for-like comparison is still too
    // noisy - a single missed Monday would otherwise immediately scream "50% less".
    private const int AnomalyMinDaysElapsed = 3;

    private async Task BuildLatestNoteAsync(List<CourseDto> allCourses)
    {
        // Active-programme scope: general notes (no CourseId) always stay visible,
        // course-bound ones only if the course belongs to the active programme (allCourses is already
        // programme-scoped, see LoadDataAsync).
        var allowedCourseIds = allCourses.Select(c => c.Id).ToHashSet();
        var notes = (await State.GetJsonCachedAsync<List<NoteDto>>("api/notes") ?? new())
            .Where(n => !n.CourseId.HasValue || allowedCourseIds.Contains(n.CourseId.Value))
            .ToList();
        _notesCount = notes.Count; // reused by the "Notes taken" achievement category (Task 6)
        _latestNote = notes.FirstOrDefault(); // server sorts by UpdatedAt descending
        if (_latestNote == null) return;

        _latestNoteExcerpt = _latestNote.Content.Length > 120
            ? _latestNote.Content[..120].TrimEnd() + "…"
            : _latestNote.Content;
        _latestNoteCourseName = _latestNote.CourseId.HasValue
            ? allCourses.FirstOrDefault(c => c.Id == _latestNote.CourseId)?.Name
            : null;
    }

    private void BuildNeglectedCourse(UserSettings settings, List<CourseDto> allCourses, List<StudySessionDto> completedHistory)
    {
        var today = DateTime.Today;
        var pick = StudyMetrics.CalcNeglectedCourse(
            allCourses, settings.SelectedCourseIds, settings.CompletedCourseIds, completedHistory, today);
        if (pick == null)
        {
            _neglectedCourse = null;
            return;
        }

        _neglectedCourse = new DashboardNeglectedCourseCard.NeglectedCourse(pick.Value.Course.Name, pick.Value.Course.Icon, pick.Value.Course.Color);
        _neglectedCourseHint = pick.Value.LastStudied.HasValue
            ? string.Format(T.LastStudiedDaysAgo ?? "", (today - pick.Value.LastStudied.Value.Date).Days)
            : string.Format(T.NotStudiedYet ?? "", StudyMetrics.NeglectedCourseHistoryDays);
    }

    // Productivity hint: sums the studied hours (same "studied" definition as
    // completedHistory above) per time-of-day bucket - EXACTLY the same bucket boundaries and the
    // same assignment as Stats.razor's BuildWeekdayAndTimeOfDay (a session counts entirely toward the
    // bucket of its start hour, even if it runs past the boundary). Four states:
    // (a) something is already planned today in the best bucket -> confirm, (b) nothing planned
    // yet and the bucket window (intersected with the study window) isn't entirely in the
    // past yet today -> suggest a plan with a calendar link, (c) window over or not a study day
    // per StudyDays -> just the neutral insight, (d) too little data (<10 sessions) or no
    // clear pattern (best bucket <30% share) -> hide the card entirely.
    private void BuildProductivityHint(List<StudySessionDto> studiedHistory, UserSettings settings, List<StudySession> todaySessions)
    {
        _productivityHintVisible = false;
        _productivityPlanned = false;
        _productivityStatus = null;
        _productivityShowPlanLink = false;

        if (studiedHistory.Count < ProductivityMinSessions) return;

        // Bucket boundaries copied 1:1 from Stats.razor (BuildWeekdayAndTimeOfDay).
        var buckets = new (int From, int To)[] { (0, 6), (6, 9), (9, 12), (12, 15), (15, 18), (18, 21), (21, 24) };
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
        if (total <= 0) return;
        var bestIdx = 0;
        for (var i = 1; i < hoursByBucket.Length; i++)
            if (hoursByBucket[i] > hoursByBucket[bestIdx]) bestIdx = i;
        if (hoursByBucket[bestIdx] / total < ProductivityMinShare) return;

        _productivityHintVisible = true;
        var bucketNames = new[]
        {
            T.ProductivityBucketNight, T.ProductivityBucketEarlyMorning, T.ProductivityBucketMorning,
            T.ProductivityBucketMidday, T.ProductivityBucketAfternoon, T.ProductivityBucketEvening,
            T.ProductivityBucketLateEvening,
        };
        _productivityInsight = string.Format(T.ProductivityInsightFormat ?? "", bucketNames[bestIdx]);

        var today = DateTime.Today;
        var bucketStart = today.AddHours(buckets[bestIdx].From);
        var bucketEnd = today.AddHours(buckets[bestIdx].To);

        // (a) A session already overlaps the best time window today -> confirm. Deliberately
        // before the StudyDays check: confirming a session that's actually planned is correct even on a
        // non-study day; only a plan SUGGESTION would be out of place there.
        var plannedSession = todaySessions
            .Where(s => s.StartTime < bucketEnd && s.EndTime > bucketStart)
            .OrderBy(s => s.StartTime)
            .FirstOrDefault();
        if (plannedSession != null)
        {
            _productivityPlanned = true;
            _productivityStatus = string.Format(T.ProductivityPlannedFormat ?? "", plannedSession.StartTime.ToString("HH:mm"));
            return;
        }

        // (c) No configured study day -> neutral insight without a call to action.
        if (!StudyPlanner.ParseStudyDays(settings.StudyDays).Contains(today.DayOfWeek)) return;

        // (b)/(c): Only suggest if the bucket intersected with the study window isn't
        // entirely over yet today (and the study window even touches the bucket at all).
        var effectiveStart = Math.Max(buckets[bestIdx].From, settings.StudyWindowStartHour);
        var effectiveEnd = Math.Min(buckets[bestIdx].To, settings.StudyWindowEndHour);
        if (effectiveEnd <= effectiveStart || DateTime.Now >= today.AddHours(effectiveEnd)) return;

        _productivityStatus = T.ProductivitySuggestText;
        _productivityShowPlanLink = true;
    }

    // Second, simpler insight alongside the time-of-day pattern above (Task 4): which weekday has the
    // most studied hours (same "studied" definition as completedHistory)? Purely informational,
    // without plan-suggestion/confirmation logic - rotates with the time-of-day hint via _insightVariant.
    private void BuildWeekdayInsight(List<StudySessionDto> studiedHistory)
    {
        _weekdayInsightAvailable = false;
        if (studiedHistory.Count < ProductivityMinSessions) return;

        var hoursByWeekday = new double[7]; // 0=Monday .. 6=Sunday, same convention as dowOffset above
        foreach (var s in studiedHistory)
        {
            var idx = ((int)s.StartTime.DayOfWeek + 6) % 7;
            hoursByWeekday[idx] += (s.EndTime - s.StartTime).TotalHours;
        }

        var total = hoursByWeekday.Sum();
        if (total <= 0) return;
        var bestIdx = 0;
        for (var i = 1; i < hoursByWeekday.Length; i++)
            if (hoursByWeekday[i] > hoursByWeekday[bestIdx]) bestIdx = i;
        if (hoursByWeekday[bestIdx] / total < ProductivityMinShare) return;

        _weekdayInsightAvailable = true;
        var weekdayNames = new[]
        {
            T.WeekdayMonday, T.WeekdayTuesday, T.WeekdayWednesday, T.WeekdayThursday,
            T.WeekdayFriday, T.WeekdaySaturday, T.WeekdaySunday,
        };
        _weekdayInsightText = string.Format(T.ProductivityWeekdayInsightFormat ?? "", weekdayNames[bestIdx]);
    }

    // Anomaly hint: compares the hours studied so far THIS week with the average
    // of the last AnomalyBaselineWeeks previous weeks - but strictly like-for-like: both sides count
    // only the first `daysElapsed` weekdays (Monday..today), otherwise a week that just
    // started would always lose against full previous weeks (on Monday morning it would otherwise be
    // essentially always "0% vs. average"). Uses the same "studied" definition as completedHistory
    // everywhere else in this file (StudyMetrics.IsStudied) and the same Monday week start as
    // the rest of Index.razor (StudyMetrics.WeekStartOf).
    //
    // Two safeguards against false alarms:
    //  (1) AnomalyMinBaselineWeeks previous weeks must actually come from recorded history
    //      (not lie before the first ever recorded session date) - otherwise, e.g. in the
    //      very first month of app usage, "less than usual" would be reported immediately against a
    //      "usual" that doesn't even exist.
    //  (2) The baseline average itself must exceed AnomalyMinBaselineHours - otherwise
    //      "50% of practically nothing" would be meaningless (e.g. a baseline from semester break).
    private void BuildAnomalyHint(List<StudySessionDto> completedHistory)
    {
        _showAnomalyHint = false;
        _anomalyPercentVsBaseline = 0;

        var today = DateTime.Today;
        var weekStart = StudyMetrics.WeekStartOf(today);
        var daysElapsed = (today - weekStart).Days + 1; // 1 (Monday) .. 7 (Sunday)
        if (daysElapsed < AnomalyMinDaysElapsed) return;

        if (completedHistory.Count == 0) return;
        var earliestWeekStart = StudyMetrics.WeekStartOf(completedHistory.Min(s => s.StartTime.Date));

        double PartialWeekHours(DateTime wStart) => completedHistory
            .Where(s => s.StartTime.Date >= wStart && s.StartTime.Date < wStart.AddDays(daysElapsed))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var baselineHours = new List<double>();
        for (var i = 1; i <= AnomalyBaselineWeeks; i++)
        {
            var wStart = weekStart.AddDays(-7 * i);
            if (wStart < earliestWeekStart) break; // older weeks lie before the start of recording
            baselineHours.Add(PartialWeekHours(wStart));
        }
        if (baselineHours.Count < AnomalyMinBaselineWeeks) return;

        var baselineAvg = baselineHours.Average();
        if (baselineAvg < AnomalyMinBaselineHours) return;

        var currentHours = PartialWeekHours(weekStart);
        var ratio = currentHours / baselineAvg;
        if (ratio >= AnomalyThresholdRatio) return;

        _showAnomalyHint = true;
        _anomalyPercentVsBaseline = (int)Math.Round(ratio * 100);
    }

    private void BuildMiniDonut(List<StudySessionDto> completedHistory, List<CourseDto> allCourses)
    {
        var cutoff = DateTime.Today.AddDays(-MiniDonutDays);
        var byCourse = completedHistory
            .Where(s => s.StartTime.Date >= cutoff)
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .Where(x => x.Hours > 0)
            .OrderByDescending(x => x.Hours)
            .ToList();

        var total = byCourse.Sum(x => x.Hours);
        _miniDonutTotalHours = total;
        if (total <= 0)
        {
            _miniDonutSlices = new();
            _miniDonutGradient = "";
            return;
        }

        const int maxLegend = 4;
        var top = byCourse.Take(maxLegend).ToList();
        var otherHours = byCourse.Skip(maxLegend).Sum(x => x.Hours);

        var slices = new List<DashboardCourseDonutCard.DonutSlice>();
        foreach (var (courseId, hours) in top)
        {
            var course = allCourses.FirstOrDefault(c => c.Id == courseId);
            slices.Add(new DashboardCourseDonutCard.DonutSlice(course?.Name ?? string.Format(T.CourseFallbackName ?? "", courseId), course?.Color ?? "#888888", hours, hours / total * 100));
        }
        if (otherHours > 0)
            slices.Add(new DashboardCourseDonutCard.DonutSlice(T.OtherCoursesSlice, "#7a7a8c", otherHours, otherHours / total * 100));

        _miniDonutSlices = slices;

        var parts = new List<string>();
        var cursor = 0.0;
        foreach (var s in slices)
        {
            var start = cursor;
            var end = cursor + s.Percent;
            parts.Add($"{s.Color} {start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}% {end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}%");
            cursor = end;
        }
        _miniDonutGradient = "conic-gradient(" + string.Join(", ", parts) + ")";
    }
}
