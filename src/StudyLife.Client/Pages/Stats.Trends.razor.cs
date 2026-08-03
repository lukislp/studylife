using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private bool _forecastAvailable;
    private bool _forecastAlreadyDone;
    private string _forecastDateLabel = "";
    private string _monthCompDeltaLabel = "0h";
    private bool _monthCompUp;
    private List<StatsEctsTimelineCard.TimelinePoint> _ectsTimelinePoints = new();
    private List<StatsProductivityScoreCard.WeekPoint> _productivityWeeks = new();
    private List<StatsGoalHistoryCard.WeekMarker> _goalHistoryWeeks = new();
    private List<StatsInactivityTrendCard.WeekBar> _inactivityWeeks = new();
    private List<StatsSessionLengthHistogramCard.LengthBucket> _sessionLengthBuckets = new();
    private List<StatsEctsPlanCard.PlanPoint> _ectsPlanPoints = new();

    private void BuildEctsTimeline(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Cumulative ECTS over time ("progress timeline"): each point is a completed goal with
        // a date, height = sum of ECTS of all courses completed up to and including that point.
        // Goals without CompletedAt (e.g. only graded, never marked "completed") deliberately
        // don't show up here - unlike the average grade above.
        var completed = goals.Where(g => g.CompletedAt.HasValue).OrderBy(g => g.CompletedAt!.Value).ToList();
        if (completed.Count < 2)
        {
            _ectsTimelinePoints = new();
            return;
        }

        var running = 0;
        var raw = new List<(DateTime Date, int Cumulative)>();
        foreach (var g in completed)
        {
            var ects = allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5;
            running += ects;
            raw.Add((g.CompletedAt!.Value, running));
        }
        var max = Math.Max(1, raw.Max(r => r.Cumulative));
        _ectsTimelinePoints = raw
            .Select(r => new StatsEctsTimelineCard.TimelinePoint(r.Date, r.Cumulative, r.Cumulative / (double)max * 100))
            .ToList();
    }

    /// <summary>
    /// Actual-vs-target ECTS progression: actual = cumulative ECTS of completed goals (same data
    /// basis as BuildEctsTimeline above), target = linear trajectory from the first completion to
    /// the desired graduation date (UserSettings.TargetGraduationDate) reaching _ectsTotal - the
    /// same "spread remaining effort evenly over the remaining time" idea as the dashboard's
    /// graduation-goal card (Index.Forecast.razor.cs), just in ECTS instead of weekly hours.
    /// Monthly grid, horizontally scrollable like the other wide charts on this page.
    /// </summary>
    private void BuildEctsPlan(List<CourseGoalDto> goals, List<CourseDto> allCourses, UserSettings settings)
    {
        _ectsPlanPoints = new();
        if (!settings.TargetGraduationDate.HasValue || _ectsTotal <= 0) return;

        var completed = goals
            .Where(g => g.CompletedAt.HasValue)
            .OrderBy(g => g.CompletedAt!.Value)
            .Select(g => (Date: g.CompletedAt!.Value.Date, Ects: allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5))
            .ToList();
        if (completed.Count == 0) return;

        var startDate = completed[0].Date;
        var targetDate = settings.TargetGraduationDate.Value.Date;
        // Target date before the first completion: no meaningful target line can be constructed.
        if (targetDate <= startDate) return;

        var today = DateTime.Today;
        var startMonth = new DateTime(startDate.Year, startDate.Month, 1);
        var endDate = targetDate > today ? targetDate : today;
        var endMonth = new DateTime(endDate.Year, endDate.Month, 1);
        // Absurd target dates (> 10 years span) would generate hundreds of columns - better to
        // show the empty state than a mile-long, meaningless chart.
        if (((endMonth.Year - startMonth.Year) * 12 + endMonth.Month - startMonth.Month) > 120) return;

        // Actual can exceed the target end value (e.g. extra courses beyond the quotas) -
        // the scale takes the maximum of both series so nothing gets clipped.
        var scaleMax = Math.Max(_ectsTotal, completed.Sum(c => c.Ects));
        var totalPlanDays = (targetDate - startDate).TotalDays;

        for (var m = startMonth; m <= endMonth; m = m.AddMonths(1))
        {
            var monthEnd = m.AddMonths(1).AddDays(-1);
            int? actualEcts = m <= today
                ? completed.Where(c => c.Date <= monthEnd).Sum(c => c.Ects)
                : null;
            var target = (int)Math.Round(Math.Clamp((monthEnd - startDate).TotalDays / totalPlanDays, 0, 1) * _ectsTotal);
            _ectsPlanPoints.Add(new StatsEctsPlanCard.PlanPoint(
                m.ToString("MM.yy"),
                actualEcts,
                actualEcts.HasValue ? Math.Min(100, actualEcts.Value / (double)scaleMax * 100) : null,
                target,
                Math.Min(100, target / (double)scaleMax * 100)));
        }
    }

    // Shared Monday-start week-bucket helper for tasks 2-4 (productivity, goal history,
    // inactivity trend) - same convention as BuildHeatmap above (dowOffset via DayOfWeek).
    private static List<DateTime> LastNWeekStarts(int weekCount)
    {
        var currentWeekStart = StudyMetrics.WeekStartOf(DateTime.Today);
        return Enumerable.Range(0, weekCount)
            .Select(i => currentWeekStart.AddDays(-7 * (weekCount - 1 - i)))
            .ToList();
    }

    private void BuildProductivityScore(List<StudySessionDto> history)
    {
        // "Productivity/engagement score": StudySessionDto has no separate planned vs. actual
        // duration (Start/EndTime ARE the entry) - instead, per week, the share of "studied"
        // sessions (IsCompleted || time elapsed) that were actively completed via the focus
        // timer (IsCompleted), rather than just having elapsed. Weeks with no studied sessions
        // at all get Percent=null (no misleading 0% bar).
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount);
        _productivityWeeks = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var studied = history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, DateTime.Now))
                .ToList();
            if (studied.Count == 0)
                return new StatsProductivityScoreCard.WeekPoint(ws.ToString("dd.MM."), null);
            var completedCount = studied.Count(s => s.IsCompleted);
            return new StatsProductivityScoreCard.WeekPoint(ws.ToString("dd.MM."), completedCount / (double)studied.Count * 100);
        }).ToList();
    }

    private void BuildGoalHistory(List<StudySessionDto> history, UserSettings settings)
    {
        // Last 12 weeks: reached when the weekly hours >= WeeklyGoalMinHours (same threshold
        // as the weekly quota on the dashboard).
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount);
        _goalHistoryWeeks = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var hours = history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, DateTime.Now))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
            return new StatsGoalHistoryCard.WeekMarker(ws, hours >= settings.WeeklyGoalMinHours, hours);
        }).ToList();
    }

    private void BuildInactivityTrend(List<StudySessionDto> history)
    {
        // Continuous hours/week as a bar chart - deliberately separate from the goal history
        // above (there, binary reached/missed as a point series), so a gradual decline
        // (even while still above the weekly goal) becomes visible.
        const int weekCount = 12;
        var weekStarts = LastNWeekStarts(weekCount);
        var weekHours = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            return history
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we && StudyMetrics.IsStudied(s, DateTime.Now))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        }).ToList();

        var maxHours = Math.Max(1, weekHours.DefaultIfEmpty(0).Max());
        _inactivityWeeks = weekStarts
            .Select((ws, i) => new StatsInactivityTrendCard.WeekBar(ws.ToString("dd.MM."), weekHours[i], Math.Min(100, weekHours[i] / maxHours * 100)))
            .ToList();
    }

    private void BuildSessionLengthHistogram(List<StudySessionDto> history)
    {
        var buckets = new (string Label, double FromMin, double ToMin)[]
        {
            ("<30m", 0, 30), ("30-60m", 30, 60), ("60-90m", 60, 90), ("90-120m", 90, 120), ("120m+", 120, double.MaxValue),
        };
        var counts = new int[buckets.Length];
        foreach (var s in history)
        {
            if (!StudyMetrics.IsStudied(s, DateTime.Now)) continue;
            var minutes = (s.EndTime - s.StartTime).TotalMinutes;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (minutes >= buckets[i].FromMin && minutes < buckets[i].ToMin) { counts[i]++; break; }
            }
        }
        var max = Math.Max(1, counts.Max());
        _sessionLengthBuckets = buckets
            .Select((b, i) => new StatsSessionLengthHistogramCard.LengthBucket(b.Label, counts[i], counts[i] / (double)max * 100))
            .ToList();
    }

    private void BuildForecast(UserSettings settings, List<CourseDto> allCourses, List<StudySessionDto> history)
    {
        // Formula and guards: see StudyMetrics.CalcForecast (shared with the dashboard).
        var forecast = StudyMetrics.CalcForecast(_ectsTotal, _ectsEarned, allCourses,
            settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, history, DateTime.Now);
        _forecastAvailable = forecast.Available;
        _forecastAlreadyDone = forecast.AlreadyDone;
        if (forecast.Available)
            _forecastDateLabel = forecast.ForecastDate!.Value.ToString("dd.MM.yyyy");
    }

    private void BuildMonthComparison(List<StudySessionDto> history)
    {
        var today = DateTime.Today;
        double HoursInMonth(int year, int month) => history
            .Where(s => s.StartTime.Year == year && s.StartTime.Month == month)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var thisMonthHours = HoursInMonth(today.Year, today.Month);
        var lastMonthDate = today.AddMonths(-1);
        var lastMonthHours = HoursInMonth(lastMonthDate.Year, lastMonthDate.Month);

        var delta = thisMonthHours - lastMonthHours;
        _monthCompUp = delta >= 0;
        var absDelta = Math.Abs(delta);
        var dH = (int)Math.Floor(absDelta);
        var dM = (int)((absDelta - dH) * 60);
        _monthCompDeltaLabel = $"{dH}h{(dM > 0 ? $" {dM}m" : "")}";
    }

    private static Dictionary<int, double?> BuildCourseTrends(List<StudySessionDto> history)
    {
        // Last 30 days vs. the 30 days before that, per course - drives the trend arrows in
        // StatsCourseListCard. Null (no arrow shown) unless there's at least 1h logged in the
        // prior window too, so a course only recently started doesn't get a misleading ±100% jump.
        var today = DateTime.Today;
        double HoursInWindow(int courseId, int fromDaysAgo, int toDaysAgo) => history
            .Where(s => s.CourseId == courseId && StudyMetrics.IsStudied(s, DateTime.Now)
                && s.StartTime.Date > today.AddDays(-fromDaysAgo)
                && s.StartTime.Date <= today.AddDays(-toDaysAgo))
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var trends = new Dictionary<int, double?>();
        foreach (var courseId in history.Select(s => s.CourseId).Distinct())
        {
            var priorHours = HoursInWindow(courseId, 60, 30);
            var lastHours = HoursInWindow(courseId, 30, 0);
            trends[courseId] = priorHours >= 1.0 ? (lastHours - priorHours) / priorHours * 100 : (double?)null;
        }
        return trends;
    }
}
