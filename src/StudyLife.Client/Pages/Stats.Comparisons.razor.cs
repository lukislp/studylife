using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsCourseComparisonChartCard.WeekGroup> _courseComparisonWeeks = new();
    private List<StatsCourseComparisonChartCard.LegendEntry> _courseComparisonLegend = new();
    // Raw facts behind _courseComparisonWeeks/_courseComparisonLegend, so RefreshCourseComparisonLabels
    // can rebuild the T.CourseFallback names without re-scanning `history`.
    private List<int> _courseComparisonTopCourseIds = new();
    private List<DateTime> _courseComparisonWeekStarts = new();
    private List<Dictionary<int, double>> _courseComparisonPerWeekPerCourse = new();
    private double _courseComparisonMaxHours = 1;
    private List<StatsNotesCorrelationCard.WeekPair> _notesCorrelationWeeks = new();
    private List<StatsCourseBalanceCard.BalanceRow> _courseBalanceRows = new();
    private List<StatsSemesterComparisonCard.MetricRow> _semesterComparisonMetrics = new();
    private string _semesterComparisonCurrentLabel = "";
    // Raw facts behind _semesterComparisonMetrics/_semesterComparisonCurrentLabel, so
    // RefreshSemesterComparisonLabels can rebuild the labels without re-scanning `allTimeHistory`.
    private bool _semesterComparisonHasData;
    private double _semesterCompCurHours;
    private decimal? _semesterCompCurGrade;
    private int _semesterCompCurEcts;
    private double _semesterCompPrevHours;
    private double? _semesterCompPrevGrade;
    private double _semesterCompPrevEcts;
    private int _semesterCompCurrentSemester;
    private double _semesterCompMaxHoursScale;
    private double _semesterCompMaxEctsScale;

    /// <summary>
    /// Multiple courses as a grouped bar chart (one cluster per week, one bar per course)
    /// over the last 12 weeks - "am I currently studying course A more than course B?" at a
    /// glance, unlike the plain per-course aggregates further below (_courseRows). Limited to
    /// the top 5 courses (by hours within the 12-week window), so the legend doesn't turn into
    /// an unreadable rainbow.
    /// </summary>
    private void BuildCourseComparison(List<StudySessionDto> history, List<CourseDto> allCourses)
    {
        const int weekCount = 12;
        const int maxCourses = 5;
        var weekStarts = LastNWeekStarts(weekCount);
        var windowStart = weekStarts[0];

        var studied = history
            .Where(s => StudyMetrics.IsStudied(s, DateTime.Now) && s.StartTime.Date >= windowStart)
            .ToList();

        var topCourseIds = studied
            .GroupBy(s => s.CourseId)
            .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .Take(maxCourses)
            .Select(x => x.CourseId)
            .ToList();

        if (topCourseIds.Count == 0)
        {
            _courseComparisonTopCourseIds = new();
            RefreshCourseComparisonLabels();
            return;
        }

        var perWeekPerCourse = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            return topCourseIds.ToDictionary(id => id, id => studied
                .Where(s => s.CourseId == id && s.StartTime.Date >= ws && s.StartTime.Date < we)
                .Sum(s => (s.EndTime - s.StartTime).TotalHours));
        }).ToList();

        // A single shared scale across all weeks/courses (instead of rescaling per week),
        // so bar heights remain genuinely comparable between weeks and courses.
        var maxHours = Math.Max(1.0, perWeekPerCourse.SelectMany(d => d.Values).DefaultIfEmpty(0).Max());

        _courseComparisonTopCourseIds = topCourseIds;
        _courseComparisonWeekStarts = weekStarts;
        _courseComparisonPerWeekPerCourse = perWeekPerCourse;
        _courseComparisonMaxHours = maxHours;
        RefreshCourseComparisonLabels();
    }

    /// <summary>Rebuilds _courseComparisonWeeks/_courseComparisonLegend from the raw per-week-
    /// per-course hour facts computed in BuildCourseComparison, using the CURRENT T and
    /// _allCourses - covers T.CourseFallback names for since-deleted courses without re-scanning
    /// `history`.</summary>
    private void RefreshCourseComparisonLabels()
    {
        if (_courseComparisonTopCourseIds.Count == 0)
        {
            _courseComparisonWeeks = new();
            _courseComparisonLegend = new();
            return;
        }

        string NameFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Name ?? string.Format(T.CourseFallback ?? "", id);
        string ColorFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Color ?? "#888888";

        _courseComparisonLegend = _courseComparisonTopCourseIds
            .Select(id => new StatsCourseComparisonChartCard.LegendEntry(NameFor(id), ColorFor(id)))
            .ToList();

        _courseComparisonWeeks = _courseComparisonWeekStarts.Select((ws, i) =>
        {
            var bars = _courseComparisonTopCourseIds
                .Select(id => new StatsCourseComparisonChartCard.CourseBar(
                    NameFor(id), ColorFor(id), _courseComparisonPerWeekPerCourse[i][id], Math.Min(100, _courseComparisonPerWeekPerCourse[i][id] / _courseComparisonMaxHours * 100)))
                .ToList();
            return new StatsCourseComparisonChartCard.WeekGroup(ws.ToString("dd.MM."), bars);
        }).ToList();
    }

    /// <summary>
    /// Notes activity vs. study time per week, last 12 weeks (same weekly grid as everywhere
    /// else in this file) - a quick "do the two move together?" glance, not a real correlation
    /// analysis. With too few notes (fewer than a handful within the window),
    /// _notesCorrelationWeeks stays empty and the card shows an empty state instead of a
    /// misleadingly sparse chart.
    /// </summary>
    private void BuildNotesCorrelation(List<StudySessionDto> history, List<NoteDto> notes)
    {
        const int weekCount = 12;
        const int minNotesInWindow = 5;
        var weekStarts = LastNWeekStarts(weekCount);
        var windowStart = weekStarts[0];

        var notesInWindow = notes.Where(n => n.CreatedAt.Date >= windowStart).ToList();
        if (notesInWindow.Count < minNotesInWindow)
        {
            _notesCorrelationWeeks = new();
            return;
        }

        var studied = history.Where(s => StudyMetrics.IsStudied(s, DateTime.Now)).ToList();

        var weekData = weekStarts.Select(ws =>
        {
            var we = ws.AddDays(7);
            var notesCount = notesInWindow.Count(n => n.CreatedAt.Date >= ws && n.CreatedAt.Date < we);
            var hours = studied
                .Where(s => s.StartTime.Date >= ws && s.StartTime.Date < we)
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);
            return (NotesCount: notesCount, Hours: hours);
        }).ToList();

        var maxNotes = Math.Max(1, weekData.Max(w => w.NotesCount));
        var maxHours = Math.Max(1.0, weekData.Max(w => w.Hours));

        _notesCorrelationWeeks = weekStarts.Select((ws, i) => new StatsNotesCorrelationCard.WeekPair(
                ws.ToString("dd.MM."),
                weekData[i].NotesCount,
                weekData[i].NotesCount / (double)maxNotes * 100,
                weekData[i].Hours,
                Math.Min(100, weekData[i].Hours / maxHours * 100)))
            .ToList();
    }

    /// <summary>
    /// Current semester directly against the average of all previous semesters (study hours,
    /// average grade, ECTS as a pace measure), semester bucketing via CourseDto.Semester as in
    /// BuildGradeHistory. Needs the ALL-TIME history instead of the 12-month `history`: sessions
    /// from earlier semesters mostly fall outside the 371-day window and would otherwise
    /// systematically push their hours average toward zero.
    /// </summary>
    private void BuildSemesterComparison(List<StudySessionDto> allTimeHistory, List<CourseGoalDto> goals, List<CourseDto> allCourses, UserSettings settings)
    {
        _semesterComparisonMetrics = new();
        _semesterComparisonHasData = false;

        var semesterByCourse = allCourses.ToDictionary(c => c.Id, c => c.Semester);

        // Current semester = most common semester among the active (selected, not completed)
        // courses - more robust than the maximum, which a single course taken ahead of schedule
        // would skew. Ties: the more advanced semester.
        var activeSemesters = settings.SelectedCourseIds
            .Except(settings.CompletedCourseIds)
            .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null)
            .Select(c => c!.Semester)
            .ToList();
        if (activeSemesters.Count == 0) return;
        var currentSemester = activeSemesters
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;

        double HoursOf(int semester) => allTimeHistory
            .Where(s => StudyMetrics.IsStudied(s, DateTime.Now)
                && semesterByCourse.TryGetValue(s.CourseId, out var sem) && sem == semester)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);
        decimal? GradeOf(int semester) => StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue && semesterByCourse.TryGetValue(g.CourseId, out var sem) && sem == semester)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.First(c => c.Id == g.CourseId).Ects)));
        int EctsOf(int semester) => allCourses
            .Where(c => c.Semester == semester && settings.CompletedCourseIds.Contains(c.Id))
            .Sum(c => c.Ects);

        // Previous semesters only count if they have any data at all - a completely empty
        // semester (lateral entry, leave of absence) would otherwise dilute the average for no reason.
        var previous = allCourses.Select(c => c.Semester)
            .Where(s => s < currentSemester)
            .Distinct()
            .Select(s => (Hours: HoursOf(s), Grade: GradeOf(s), Ects: EctsOf(s)))
            .Where(x => x.Hours > 0 || x.Grade.HasValue || x.Ects > 0)
            .ToList();
        if (previous.Count == 0) return;

        var curHours = HoursOf(currentSemester);
        var curGrade = GradeOf(currentSemester);
        var curEcts = EctsOf(currentSemester);
        var prevHours = previous.Average(p => p.Hours);
        // Average of the semester averages (each previous semester counts equally), not an
        // ECTS-weighted overall average - it's semester compared against semester.
        var prevGrades = previous.Where(p => p.Grade.HasValue).Select(p => (double)p.Grade!.Value).ToList();
        double? prevGrade = prevGrades.Count > 0 ? prevGrades.Average() : null;
        var prevEcts = previous.Average(p => (double)p.Ects);

        _semesterComparisonHasData = true;
        _semesterCompCurHours = curHours;
        _semesterCompCurGrade = curGrade;
        _semesterCompCurEcts = curEcts;
        _semesterCompPrevHours = prevHours;
        _semesterCompPrevGrade = prevGrade;
        _semesterCompPrevEcts = prevEcts;
        _semesterCompCurrentSemester = currentSemester;
        _semesterCompMaxHoursScale = Math.Max(1.0, Math.Max(curHours, prevHours));
        _semesterCompMaxEctsScale = Math.Max(1.0, Math.Max(curEcts, prevEcts));
        RefreshSemesterComparisonLabels();
    }

    /// <summary>Rebuilds _semesterComparisonMetrics/_semesterComparisonCurrentLabel from the raw
    /// hours/grade/ECTS facts computed in BuildSemesterComparison, using the CURRENT T - avoids
    /// re-scanning `allTimeHistory` on a live language switch.</summary>
    private void RefreshSemesterComparisonLabels()
    {
        if (!_semesterComparisonHasData) return;

        static string HoursLabel(double h) => $"{(int)h}h {(int)((h - (int)h) * 60)}m";
        static string GradeLabel(double? g) => g.HasValue
            ? StudyMetrics.FormatGrade((decimal)g.Value)
            : "–";
        // Grade bar inverted like everywhere else on this page (1.0 fills, 5.0 stays empty);
        // missing grade = "–" with an empty bar instead of a misleading zero value.
        static double GradeBar(double? g) => g.HasValue ? Math.Clamp((5.0 - g.Value) / 4.0 * 100, 0, 100) : 0;

        var curGrade = _semesterCompCurGrade.HasValue ? (double)_semesterCompCurGrade.Value : (double?)null;
        _semesterComparisonMetrics = new()
        {
            new(T.SemesterCompHours ?? "", HoursLabel(_semesterCompCurHours), _semesterCompCurHours / _semesterCompMaxHoursScale * 100,
                HoursLabel(_semesterCompPrevHours), _semesterCompPrevHours / _semesterCompMaxHoursScale * 100),
            new(T.SemesterCompGrade ?? "", GradeLabel(curGrade), GradeBar(curGrade),
                GradeLabel(_semesterCompPrevGrade), GradeBar(_semesterCompPrevGrade)),
            new(T.SemesterCompEcts ?? "", _semesterCompCurEcts.ToString(), _semesterCompCurEcts / _semesterCompMaxEctsScale * 100,
                _semesterCompPrevEcts.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','), _semesterCompPrevEcts / _semesterCompMaxEctsScale * 100),
        };
        _semesterComparisonCurrentLabel = string.Format(T.SemesterComparisonCurrentFormat ?? "", _semesterCompCurrentSemester);
    }

    /// <summary>
    /// ECTS-weighted target time share vs. actual time share per active course - which courses
    /// get more/less study time than would be "fair" relative to their credit weight? Only
    /// selected, not-yet-completed courses (same active definition as the isCompleted flag in
    /// _courseRows, Stats.razor.cs) - completed courses would otherwise needlessly skew the
    /// target share without any further invested time making sense.
    /// </summary>
    private void BuildCourseBalance(List<StudySessionDto> history, List<CourseDto> allCourses, UserSettings settings)
    {
        var activeIds = settings.SelectedCourseIds.Except(settings.CompletedCourseIds).ToList();
        var activeCourses = activeIds
            .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();

        if (activeCourses.Count == 0)
        {
            _courseBalanceRows = new();
            return;
        }

        var totalEcts = activeCourses.Sum(c => c.Ects);
        var studied = history.Where(s => StudyMetrics.IsStudied(s, DateTime.Now) && activeIds.Contains(s.CourseId)).ToList();
        var hoursByCourse = activeIds.ToDictionary(id => id, id => studied.Where(s => s.CourseId == id).Sum(s => (s.EndTime - s.StartTime).TotalHours));
        var totalHours = hoursByCourse.Values.Sum();

        _courseBalanceRows = activeCourses
            .Select(c =>
            {
                var targetPercent = totalEcts > 0 ? c.Ects / (double)totalEcts * 100 : 0;
                var actualPercent = totalHours > 0 ? hoursByCourse[c.Id] / totalHours * 100 : 0;
                return new StatsCourseBalanceCard.BalanceRow(c.Name, c.Icon, c.Color, targetPercent, actualPercent);
            })
            // Largest deviation first - the most interesting rows (most over-/under-invested) on top.
            .OrderByDescending(r => Math.Abs(r.DeltaPercent))
            .ToList();
    }
}
