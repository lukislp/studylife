using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<List<StatsHeatmapCard.HeatDay>> _heatmapWeeks = new();
    private List<string> _heatmapMonthLabels = new();
    // Per-date course ids behind _heatmapWeeks' day.Courses, same order (desc by hours) - lets
    // RefreshHeatmapCourseNames rebuild T.CourseFallback names for since-deleted courses on a
    // language switch without re-scanning `history`.
    private Dictionary<DateTime, List<int>> _heatmapCourseIdsByDate = new();
    private List<StatsCourseDonutCard.DonutSlice> _donutSlices = new();
    private List<int> _donutCourseIds = new(); // parallel to _donutSlices, for the same reason as above
    private string _donutGradient = "";
    private double _donutTotalHours;
    private List<StatsRhythmCard.BarPoint> _weekdayHours = new();
    private double[] _weekdayHoursRaw = new double[7];
    private double _weekdayMaxRaw = 1;
    private List<StatsRhythmCard.BarPoint> _timeOfDayHours = new();
    private List<List<StatsTimeHeatmapCard.TimeHeatCell>> _timeHeatmapRows = new();
    // Raw per-(weekday,hour)-cell facts behind _timeHeatmapRows, so RefreshTimeHeatmapRows can
    // rebuild both the weekday labels and the course-fallback names without re-scanning `history`.
    private double[,] _timeHeatmapHoursByCell = new double[7, 24];
    private int[,] _timeHeatmapSessionCountByCell = new int[7, 24];
    private Dictionary<(int Weekday, int Hour), Dictionary<int, double>> _timeHeatmapCourseHoursByCell = new();
    private double _timeHeatmapMaxCell = 1;
    private List<StatsMonthlyBreakdownCard.StackedMonth> _monthlyStacks = new();
    private List<StatsMonthlyBreakdownCard.LegendEntry> _monthlyLegend = new();
    // Raw per-month-per-course hour facts behind _monthlyStacks/_monthlyLegend, so
    // RefreshMonthlyBreakdown can rebuild the T.Other/T.CourseFallback labels without
    // re-scanning `history`.
    private List<DateTime> _monthlyMonthStarts = new();
    private List<Dictionary<int, double>> _monthlyPerMonthCourseHours = new();
    private List<int> _monthlyOrderedIds = new();
    private HashSet<int> _monthlyTopIds = new();
    private double _monthlyMaxMonthTotal = 1;

    private void BuildHeatmap(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        const int totalWeeks = 53;
        var hoursByDate = history
            .GroupBy(s => s.StartTime.Date)
            .ToDictionary(g => g.Key, g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours));
        // Per-course breakdown per day for the click popover - separate from hoursByDate because
        // most days (level 0/-1) don't need it at all and we want to save the GroupBy cost.
        var byDateAndCourseRaw = history
            .GroupBy(s => s.StartTime.Date)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.CourseId)
                    .Select(cg => (CourseId: cg.Key, Hours: cg.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
                    .OrderByDescending(c => c.Hours)
                    .ToList());
        _heatmapCourseIdsByDate = byDateAndCourseRaw.ToDictionary(kv => kv.Key, kv => kv.Value.Select(c => c.CourseId).ToList());
        var byDateAndCourse = byDateAndCourseRaw.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(c =>
            {
                var course = allCourses.FirstOrDefault(x => x.Id == c.CourseId);
                return new StatsHeatmapCard.CourseHours(
                    course?.Name ?? string.Format(T.CourseFallback, c.CourseId),
                    course?.Color ?? "#888888",
                    c.Hours);
            }).ToList());
        var sessionCountByDate = history.GroupBy(s => s.StartTime.Date).ToDictionary(g => g.Key, g => g.Count());

        var today = DateTime.Today;
        var currentWeekStart = StudyMetrics.WeekStartOf(today);
        var gridStart = currentWeekStart.AddDays(-7 * (totalWeeks - 1));

        _heatmapWeeks = new();
        _heatmapMonthLabels = new();
        var lastMonth = -1;
        for (var w = 0; w < totalWeeks; w++)
        {
            var weekStart = gridStart.AddDays(7 * w);
            var days = new List<StatsHeatmapCard.HeatDay>();
            for (var d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                if (date > today)
                {
                    days.Add(new StatsHeatmapCard.HeatDay(date, 0, -1, 0, new()));
                    continue;
                }
                var hours = hoursByDate.TryGetValue(date, out var h) ? h : 0;
                var level = hours <= 0 ? 0 : hours < 1 ? 1 : hours < 2 ? 2 : hours < 4 ? 3 : 4;
                var sessionCount = sessionCountByDate.TryGetValue(date, out var sc) ? sc : 0;
                var courses = byDateAndCourse.TryGetValue(date, out var cs) ? cs : new();
                days.Add(new StatsHeatmapCard.HeatDay(date, hours, level, sessionCount, courses));
            }
            _heatmapWeeks.Add(days);
            _heatmapMonthLabels.Add(weekStart.Month != lastMonth ? weekStart.ToString("MMM") : "");
            lastMonth = weekStart.Month;
        }
    }

    /// <summary>Re-resolves the Name of every course entry in _heatmapWeeks' HeatDay.Courses lists
    /// from _heatmapCourseIdsByDate + the current T/_allCourses - only T.CourseFallback names for
    /// since-deleted courses actually change here, real course names are already up to date.</summary>
    private void RefreshHeatmapCourseNames()
    {
        foreach (var week in _heatmapWeeks)
        {
            for (var i = 0; i < week.Count; i++)
            {
                var day = week[i];
                if (!_heatmapCourseIdsByDate.TryGetValue(day.Date, out var ids) || ids.Count != day.Courses.Count) continue;
                var courses = day.Courses.Select((c, idx) =>
                {
                    var course = _allCourses.FirstOrDefault(x => x.Id == ids[idx]);
                    return c with { Name = course?.Name ?? string.Format(T.CourseFallback ?? "", ids[idx]) };
                }).ToList();
                week[i] = day with { Courses = courses };
            }
        }
    }

    private void BuildDonut(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        var byCourse = history
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Sessions: g.ToList(), Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .Where(x => x.Hours > 0)
            .OrderByDescending(x => x.Hours)
            .ToList();

        var total = byCourse.Sum(x => x.Hours);
        _donutTotalHours = total;
        if (total <= 0)
        {
            _donutSlices = new();
            _donutCourseIds = new();
            _donutGradient = "";
            return;
        }

        // Embed the monthly mini-chart + recent sessions for the click drilldown directly in the
        // slice (same idea as BuildHeatmap's byDateAndCourse: the card gets everything up front
        // instead of having to reload on click).
        const int monthCount = 12;
        const int recentSessionCount = 8;
        var today = DateTime.Today;
        var monthStarts = Enumerable.Range(0, monthCount)
            .Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(-(monthCount - 1 - i)))
            .ToList();

        _donutSlices = byCourse
            .Select(x =>
            {
                var course = allCourses.FirstOrDefault(c => c.Id == x.CourseId);
                var perMonth = monthStarts
                    .Select(m => x.Sessions
                        .Where(s => s.StartTime.Year == m.Year && s.StartTime.Month == m.Month)
                        .Sum(s => (s.EndTime - s.StartTime).TotalHours))
                    .ToList();
                // Scale relative to THIS course's strongest month - the drilldown shows a
                // course's rhythm, not a cross-course comparison (that's what the monthly
                // breakdown further below is for).
                var maxMonth = perMonth.Max();
                var months = monthStarts
                    .Select((m, i) => new StatsCourseDonutCard.MonthHours(
                        m.ToString("MMM"), perMonth[i], maxMonth > 0 ? perMonth[i] / maxMonth * 100 : 0))
                    .ToList();
                var recent = x.Sessions
                    .OrderByDescending(s => s.StartTime)
                    .Take(recentSessionCount)
                    .Select(s => new StatsCourseDonutCard.SessionEntry(s.StartTime, s.EndTime, s.Topic))
                    .ToList();
                return new StatsCourseDonutCard.DonutSlice(
                    course?.Name ?? string.Format(T.CourseFallback ?? "", x.CourseId), course?.Color ?? "#888888",
                    x.Hours, x.Hours / total * 100, x.Sessions.Count, months, recent);
            })
            .ToList();
        _donutCourseIds = byCourse.Select(x => x.CourseId).ToList();

        var parts = new List<string>();
        var cursor = 0.0;
        foreach (var s in _donutSlices)
        {
            var start = cursor;
            var end = cursor + s.Percent;
            parts.Add($"{s.Color} {start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}% {end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}%");
            cursor = end;
        }
        _donutGradient = "conic-gradient(" + string.Join(", ", parts) + ")";
    }

    /// <summary>Re-resolves the Name of every slice in _donutSlices from _donutCourseIds + the
    /// current T/_allCourses - only T.CourseFallback names for since-deleted courses change here.</summary>
    private void RefreshDonutCourseNames()
    {
        if (_donutSlices.Count != _donutCourseIds.Count) return;
        _donutSlices = _donutSlices.Select((slice, i) =>
        {
            var course = _allCourses.FirstOrDefault(c => c.Id == _donutCourseIds[i]);
            return slice with { Name = course?.Name ?? string.Format(T.CourseFallback ?? "", _donutCourseIds[i]) };
        }).ToList();
    }

    private void BuildWeekdayAndTimeOfDay(List<StudySessionDto> history)
    {
        var hoursByWeekday = new double[7];
        foreach (var s in history)
        {
            var idx = ((int)s.StartTime.DayOfWeek + 6) % 7; // Monday = 0
            hoursByWeekday[idx] += (s.EndTime - s.StartTime).TotalHours;
        }
        _weekdayHoursRaw = hoursByWeekday;
        _weekdayMaxRaw = Math.Max(1, hoursByWeekday.Max());
        RefreshWeekdayHours();

        var buckets = new (string Label, int From, int To)[]
        {
            ("00-06", 0, 6), ("06-09", 6, 9), ("09-12", 9, 12), ("12-15", 12, 15),
            ("15-18", 15, 18), ("18-21", 18, 21), ("21-24", 21, 24),
        };
        var hoursByBucket = new double[buckets.Length];
        foreach (var s in history)
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
        var maxBucket = Math.Max(1, hoursByBucket.Max());
        _timeOfDayHours = buckets
            .Select((b, i) => new StatsRhythmCard.BarPoint(b.Label, hoursByBucket[i], Math.Min(100, hoursByBucket[i] / maxBucket * 100)))
            .ToList();
    }

    /// <summary>Rebuilds _weekdayHours from _weekdayHoursRaw/_weekdayMaxRaw + the current T - the
    /// underlying per-weekday hour totals don't need re-scanning `history`, only the weekday NAMES
    /// (T.WeekdayMon..Sun) are stale after a live language switch.</summary>
    private void RefreshWeekdayHours()
    {
        var weekdayNames = new[] { T.WeekdayMon, T.WeekdayTue, T.WeekdayWed, T.WeekdayThu, T.WeekdayFri, T.WeekdaySat, T.WeekdaySun };
        _weekdayHours = weekdayNames
            .Select((name, i) => new StatsRhythmCard.BarPoint(name, _weekdayHoursRaw[i], Math.Min(100, _weekdayHoursRaw[i] / _weekdayMaxRaw * 100)))
            .ToList();
    }

    private void BuildTimeHeatmap(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        // Same "attribute the whole session to its start hour" bucketing BuildWeekdayAndTimeOfDay
        // already uses (no minute-by-minute splitting across hour boundaries) - kept consistent
        // with that sibling method rather than introducing a more precise but inconsistent approach.
        var hoursByCell = new double[7, 24];
        var sessionCountByCell = new int[7, 24];
        // Per-course breakdown per cell for the click detail panel - same idea as
        // BuildHeatmap's byDateAndCourse, just with (weekday, hour) instead of date as the key.
        var byCellAndCourse = new Dictionary<(int Weekday, int Hour), Dictionary<int, double>>();
        foreach (var s in history)
        {
            var weekdayIdx = ((int)s.StartTime.DayOfWeek + 6) % 7; // Monday = 0
            var sessionHours = (s.EndTime - s.StartTime).TotalHours;
            hoursByCell[weekdayIdx, s.StartTime.Hour] += sessionHours;
            sessionCountByCell[weekdayIdx, s.StartTime.Hour]++;
            var key = (weekdayIdx, s.StartTime.Hour);
            if (!byCellAndCourse.TryGetValue(key, out var perCourse))
                byCellAndCourse[key] = perCourse = new();
            perCourse[s.CourseId] = perCourse.GetValueOrDefault(s.CourseId) + sessionHours;
        }

        // Unlike BuildHeatmap's per-CALENDAR-DAY levels (fixed <1/<2/<4h cutoffs make sense there,
        // since one day tops out around a handful of hours), a weekday+hour cell here sums up to
        // ~53 occurrences of that slot across the whole history window. Reusing those same absolute
        // cutoffs would push nearly every regularly-used slot straight to the top level and erase
        // the pattern this chart exists to show - so levels are relative to this grid's own max cell
        // instead, split into quarters of that max (still the same 5-level look/CSS as the other heatmap).
        var maxCell = 1.0;
        foreach (var h in hoursByCell)
            if (h > maxCell) maxCell = h;

        _timeHeatmapHoursByCell = hoursByCell;
        _timeHeatmapSessionCountByCell = sessionCountByCell;
        _timeHeatmapCourseHoursByCell = byCellAndCourse;
        _timeHeatmapMaxCell = maxCell;
        RefreshTimeHeatmapRows();
    }

    /// <summary>Rebuilds _timeHeatmapRows from the raw per-cell hour/session/course facts computed
    /// in BuildTimeHeatmap, using the CURRENT T and _allCourses - covers both the weekday labels
    /// and any T.CourseFallback names for since-deleted courses, without re-scanning `history`.</summary>
    private void RefreshTimeHeatmapRows()
    {
        var weekdayNames = new[] { T.WeekdayMon, T.WeekdayTue, T.WeekdayWed, T.WeekdayThu, T.WeekdayFri, T.WeekdaySat, T.WeekdaySun };
        _timeHeatmapRows = new();
        for (var w = 0; w < 7; w++)
        {
            var row = new List<StatsTimeHeatmapCard.TimeHeatCell>();
            for (var h = 0; h < 24; h++)
            {
                var hours = _timeHeatmapHoursByCell[w, h];
                var level = hours <= 0 ? 0 : hours < _timeHeatmapMaxCell * 0.25 ? 1 : hours < _timeHeatmapMaxCell * 0.5 ? 2 : hours < _timeHeatmapMaxCell * 0.75 ? 3 : 4;
                var courses = _timeHeatmapCourseHoursByCell.TryGetValue((w, h), out var perCourse)
                    ? perCourse
                        .Select(kv =>
                        {
                            var course = _allCourses.FirstOrDefault(c => c.Id == kv.Key);
                            return new StatsHeatmapCard.CourseHours(
                                course?.Name ?? string.Format(T.CourseFallback ?? "", kv.Key),
                                course?.Color ?? "#888888",
                                kv.Value);
                        })
                        .OrderByDescending(c => c.Hours)
                        .ToList()
                    : new List<StatsHeatmapCard.CourseHours>();
                row.Add(new StatsTimeHeatmapCard.TimeHeatCell(weekdayNames[w], h, hours, level, _timeHeatmapSessionCountByCell[w, h], courses));
            }
            _timeHeatmapRows.Add(row);
        }
    }

    private void BuildMonthlyStacks(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        const int monthCount = 6;
        var today = DateTime.Today;
        var monthStarts = Enumerable.Range(0, monthCount)
            .Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(-(monthCount - 1 - i)))
            .ToList();

        var perMonthCourseHours = monthStarts.Select(_ => new Dictionary<int, double>()).ToList();
        foreach (var s in history)
        {
            var monthStart = new DateTime(s.StartTime.Year, s.StartTime.Month, 1);
            var idx = monthStarts.FindIndex(m => m == monthStart);
            if (idx < 0) continue;
            var dict = perMonthCourseHours[idx];
            dict[s.CourseId] = dict.GetValueOrDefault(s.CourseId) + (s.EndTime - s.StartTime).TotalHours;
        }

        var totalsByCourse = new Dictionary<int, double>();
        foreach (var dict in perMonthCourseHours)
            foreach (var (courseId, hours) in dict)
                totalsByCourse[courseId] = totalsByCourse.GetValueOrDefault(courseId) + hours;

        var orderedIds = totalsByCourse.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var topIds = orderedIds.Take(6).ToHashSet();
        var maxMonthTotal = Math.Max(1, perMonthCourseHours.Select(d => d.Values.Sum()).DefaultIfEmpty(0).Max());

        _monthlyMonthStarts = monthStarts;
        _monthlyPerMonthCourseHours = perMonthCourseHours;
        _monthlyOrderedIds = orderedIds;
        _monthlyTopIds = topIds;
        _monthlyMaxMonthTotal = maxMonthTotal;
        RefreshMonthlyBreakdown();
    }

    /// <summary>Rebuilds _monthlyLegend/_monthlyStacks from the raw per-month-per-course hour facts
    /// computed in BuildMonthlyStacks, using the CURRENT T and _allCourses - covers both the
    /// T.Other long-tail label and T.CourseFallback names for since-deleted courses, without
    /// re-scanning `history`.</summary>
    private void RefreshMonthlyBreakdown()
    {
        const string otherColor = "#7a7a8c";
        string NameFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Name ?? string.Format(T.CourseFallback ?? "", id);
        string ColorFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Color ?? "#888888";

        _monthlyLegend = _monthlyOrderedIds.Where(id => _monthlyTopIds.Contains(id)).Select(id => new StatsMonthlyBreakdownCard.LegendEntry(NameFor(id), ColorFor(id))).ToList();
        if (_monthlyOrderedIds.Any(id => !_monthlyTopIds.Contains(id)))
            _monthlyLegend.Add(new StatsMonthlyBreakdownCard.LegendEntry(T.Other, otherColor));

        _monthlyStacks = _monthlyMonthStarts.Select((m, i) =>
        {
            var dict = _monthlyPerMonthCourseHours[i];
            var segments = new List<StatsMonthlyBreakdownCard.StackSegment>();
            foreach (var id in _monthlyOrderedIds.Where(id => _monthlyTopIds.Contains(id)))
            {
                if (!dict.TryGetValue(id, out var hours) || hours <= 0) continue;
                segments.Add(new StatsMonthlyBreakdownCard.StackSegment(NameFor(id), ColorFor(id), hours, hours / _monthlyMaxMonthTotal * 100));
            }
            var otherHours = _monthlyOrderedIds.Where(id => !_monthlyTopIds.Contains(id)).Sum(id => dict.GetValueOrDefault(id));
            if (otherHours > 0)
                segments.Add(new StatsMonthlyBreakdownCard.StackSegment(T.Other, otherColor, otherHours, otherHours / _monthlyMaxMonthTotal * 100));

            var total = dict.Values.Sum();
            return new StatsMonthlyBreakdownCard.StackedMonth(m.ToString("MMM"), segments, $"{(int)total}h {(int)((total - (int)total) * 60)}m");
        }).ToList();
    }
}
