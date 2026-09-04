using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<List<StatsHeatmapCard.HeatDay>> _heatmapWeeks = new();
    private List<string> _heatmapMonthLabels = new();
    // Per-date course ids behind _heatmapWeeks' day.Courses, same order (desc by hours) - lets
    // RefreshHeatmapCourseNames rebuild T.CourseFallback names for since-deleted courses on a
    // language switch without re-scanning the builder result.
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
    // rebuild both the weekday labels and the course-fallback names without re-running the builder.
    private double[,] _timeHeatmapHoursByCell = new double[7, 24];
    private int[,] _timeHeatmapSessionCountByCell = new int[7, 24];
    private Dictionary<(int Weekday, int Hour), List<StatsCourseHoursDto>> _timeHeatmapCourseHoursByCell = new();
    private double _timeHeatmapMaxCell = 1;
    private List<StatsMonthlyBreakdownCard.StackedMonth> _monthlyStacks = new();
    private List<StatsMonthlyBreakdownCard.LegendEntry> _monthlyLegend = new();
    // Raw per-month-per-course hour facts behind _monthlyStacks/_monthlyLegend, so
    // RefreshMonthlyBreakdown can rebuild the T.Other/T.CourseFallback labels without re-running
    // the builder.
    private List<DateTime> _monthlyMonthStarts = new();
    private List<Dictionary<int, double>> _monthlyPerMonthCourseHours = new();
    private List<int> _monthlyOrderedIds = new();
    private HashSet<int> _monthlyTopIds = new();
    private double _monthlyMaxMonthTotal = 1;

    private void ApplyCharts(StatsCoreSummaryDto core)
    {
        ApplyHeatmap(core.Heatmap);
        ApplyDonut(core.Donut);
        ApplyRhythm(core.Rhythm);
        ApplyTimeHeatmap(core.TimeHeatmap);
        ApplyMonthlyBreakdown(core.MonthlyBreakdown);
    }

    /// <summary>Course name as the charts show it: the catalog name, or the localized
    /// "course #{id}" fallback for a since-deleted course. The only part of a chart's course
    /// entries the builder deliberately leaves to the client (the color is not localized and
    /// comes ready-made).</summary>
    private string CourseNameOrFallback(int courseId) =>
        _allCourses.FirstOrDefault(c => c.Id == courseId)?.Name ?? string.Format(T.CourseFallback ?? "", courseId);

    private StatsHeatmapCard.CourseHours ToCourseHours(StatsCourseHoursDto c) =>
        new(CourseNameOrFallback(c.CourseId), c.Color, c.Hours);

    private void ApplyHeatmap(StatsHeatmapDto heatmap)
    {
        _heatmapWeeks = heatmap.Weeks
            .Select(w => w.Days
                .Select(d => new StatsHeatmapCard.HeatDay(
                    d.Date, d.Hours, d.Level, d.SessionCount, d.Courses.Select(ToCourseHours).ToList()))
                .ToList())
            .ToList();
        _heatmapCourseIdsByDate = heatmap.Weeks
            .SelectMany(w => w.Days)
            .ToDictionary(d => d.Date, d => d.Courses.Select(c => c.CourseId).ToList());
        // "MMM" is culture-dependent, so it stays on the client - the builder only marks which
        // weeks start a new month.
        _heatmapMonthLabels = heatmap.Weeks
            .Select(w => w.ShowMonthLabel ? w.WeekStart.ToString("MMM") : "")
            .ToList();
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
                var courses = day.Courses
                    .Select((c, idx) => c with { Name = CourseNameOrFallback(ids[idx]) })
                    .ToList();
                week[i] = day with { Courses = courses };
            }
        }
    }

    private void ApplyDonut(StatsDonutDto donut)
    {
        _donutTotalHours = donut.TotalHours;
        _donutGradient = donut.Gradient;
        _donutCourseIds = donut.Slices.Select(s => s.CourseId).ToList();
        _donutSlices = donut.Slices
            .Select(s => new StatsCourseDonutCard.DonutSlice(
                CourseNameOrFallback(s.CourseId), s.Color, s.Hours, s.Percent, s.SessionCount,
                // "MMM" again - the builder carries the raw month start, see ApplyHeatmap.
                s.Months.Select(m => new StatsCourseDonutCard.MonthHours(m.MonthStart.ToString("MMM"), m.Hours, m.Percent)).ToList(),
                s.RecentSessions.Select(r => new StatsCourseDonutCard.SessionEntry(r.Start, r.End, r.Topic)).ToList()))
            .ToList();
    }

    /// <summary>Re-resolves the Name of every slice in _donutSlices from _donutCourseIds + the
    /// current T/_allCourses - only T.CourseFallback names for since-deleted courses change here.</summary>
    private void RefreshDonutCourseNames()
    {
        if (_donutSlices.Count != _donutCourseIds.Count) return;
        _donutSlices = _donutSlices
            .Select((slice, i) => slice with { Name = CourseNameOrFallback(_donutCourseIds[i]) })
            .ToList();
    }

    private void ApplyRhythm(StatsRhythmDto rhythm)
    {
        _weekdayHoursRaw = rhythm.WeekdayHours.ToArray();
        _weekdayMaxRaw = rhythm.WeekdayMax;
        RefreshWeekdayHours();

        // The time-of-day bucket labels ("00-06" …) are fixed notation, not i18n text - they come
        // ready-made from the builder and need no relocalization pass.
        _timeOfDayHours = rhythm.TimeOfDay
            .Select(b => new StatsRhythmCard.BarPoint(b.Label, b.Hours, b.Percent))
            .ToList();
    }

    /// <summary>Rebuilds _weekdayHours from _weekdayHoursRaw/_weekdayMaxRaw + the current T - the
    /// underlying per-weekday hour totals don't need recomputing, only the weekday NAMES
    /// (T.WeekdayMon..Sun) are stale after a live language switch.</summary>
    private void RefreshWeekdayHours()
    {
        var weekdayNames = new[] { T.WeekdayMon, T.WeekdayTue, T.WeekdayWed, T.WeekdayThu, T.WeekdayFri, T.WeekdaySat, T.WeekdaySun };
        _weekdayHours = weekdayNames
            .Select((name, i) => new StatsRhythmCard.BarPoint(name, _weekdayHoursRaw[i], Math.Min(100, _weekdayHoursRaw[i] / _weekdayMaxRaw * 100)))
            .ToList();
    }

    private void ApplyTimeHeatmap(StatsTimeHeatmapDto timeHeatmap)
    {
        // The builder's grids are jagged (they have to survive JSON); the page keeps the [7,24]
        // arrays RefreshTimeHeatmapRows has always indexed into.
        var hoursByCell = new double[7, 24];
        var sessionCountByCell = new int[7, 24];
        for (var w = 0; w < 7; w++)
        {
            for (var h = 0; h < 24; h++)
            {
                hoursByCell[w, h] = timeHeatmap.HoursByCell[w][h];
                sessionCountByCell[w, h] = timeHeatmap.SessionCountByCell[w][h];
            }
        }

        _timeHeatmapHoursByCell = hoursByCell;
        _timeHeatmapSessionCountByCell = sessionCountByCell;
        _timeHeatmapCourseHoursByCell = timeHeatmap.CellCourses.ToDictionary(c => (c.Weekday, c.Hour), c => c.Courses);
        _timeHeatmapMaxCell = timeHeatmap.MaxCell;
        RefreshTimeHeatmapRows();
    }

    /// <summary>Rebuilds _timeHeatmapRows from the raw per-cell hour/session/course facts the
    /// builder produced, using the CURRENT T and _allCourses - covers both the weekday labels
    /// and any T.CourseFallback names for since-deleted courses.</summary>
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
                    ? perCourse.Select(ToCourseHours).ToList()
                    : new List<StatsHeatmapCard.CourseHours>();
                row.Add(new StatsTimeHeatmapCard.TimeHeatCell(weekdayNames[w], h, hours, level, _timeHeatmapSessionCountByCell[w, h], courses));
            }
            _timeHeatmapRows.Add(row);
        }
    }

    private void ApplyMonthlyBreakdown(StatsMonthlyBreakdownDto monthly)
    {
        _monthlyMonthStarts = monthly.MonthStarts;
        _monthlyPerMonthCourseHours = monthly.PerMonthCourseHours;
        _monthlyOrderedIds = monthly.OrderedIds;
        _monthlyTopIds = monthly.TopIds.ToHashSet();
        _monthlyMaxMonthTotal = monthly.MaxMonthTotal;
        RefreshMonthlyBreakdown();
    }

    /// <summary>Rebuilds _monthlyLegend/_monthlyStacks from the raw per-month-per-course hour facts
    /// the builder produced, using the CURRENT T and _allCourses - covers both the T.Other
    /// long-tail label and T.CourseFallback names for since-deleted courses.</summary>
    private void RefreshMonthlyBreakdown()
    {
        const string otherColor = "#7a7a8c";
        string ColorFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Color ?? "#888888";

        _monthlyLegend = _monthlyOrderedIds.Where(id => _monthlyTopIds.Contains(id)).Select(id => new StatsMonthlyBreakdownCard.LegendEntry(CourseNameOrFallback(id), ColorFor(id))).ToList();
        if (_monthlyOrderedIds.Any(id => !_monthlyTopIds.Contains(id)))
            _monthlyLegend.Add(new StatsMonthlyBreakdownCard.LegendEntry(T.Other, otherColor));

        _monthlyStacks = _monthlyMonthStarts.Select((m, i) =>
        {
            var dict = _monthlyPerMonthCourseHours[i];
            var segments = new List<StatsMonthlyBreakdownCard.StackSegment>();
            foreach (var id in _monthlyOrderedIds.Where(id => _monthlyTopIds.Contains(id)))
            {
                if (!dict.TryGetValue(id, out var hours) || hours <= 0) continue;
                segments.Add(new StatsMonthlyBreakdownCard.StackSegment(CourseNameOrFallback(id), ColorFor(id), hours, hours / _monthlyMaxMonthTotal * 100));
            }
            var otherHours = _monthlyOrderedIds.Where(id => !_monthlyTopIds.Contains(id)).Sum(id => dict.GetValueOrDefault(id));
            if (otherHours > 0)
                segments.Add(new StatsMonthlyBreakdownCard.StackSegment(T.Other, otherColor, otherHours, otherHours / _monthlyMaxMonthTotal * 100));

            var total = dict.Values.Sum();
            // "MMM" is culture-dependent, so it stays on the client, see ApplyHeatmap.
            return new StatsMonthlyBreakdownCard.StackedMonth(m.ToString("MMM"), segments, $"{(int)total}h {(int)((total - (int)total) * 60)}m");
        }).ToList();
    }
}
