using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsCourseComparisonChartCard.WeekGroup> _courseComparisonWeeks = new();
    private List<StatsCourseComparisonChartCard.LegendEntry> _courseComparisonLegend = new();
    // Raw facts behind _courseComparisonWeeks/_courseComparisonLegend, so RefreshCourseComparisonLabels
    // can rebuild the T.CourseFallback names without re-running the builder.
    private List<int> _courseComparisonTopCourseIds = new();
    private List<DateTime> _courseComparisonWeekStarts = new();
    private List<Dictionary<int, double>> _courseComparisonPerWeekPerCourse = new();
    private double _courseComparisonMaxHours = 1;
    private List<StatsNotesCorrelationCard.WeekPair> _notesCorrelationWeeks = new();
    private List<StatsCourseBalanceCard.BalanceRow> _courseBalanceRows = new();
    private List<StatsSemesterComparisonCard.MetricRow> _semesterComparisonMetrics = new();
    private string _semesterComparisonCurrentLabel = "";
    // Raw facts behind _semesterComparisonMetrics/_semesterComparisonCurrentLabel, so
    // RefreshSemesterComparisonLabels can rebuild the labels without re-running the builder.
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

    private void ApplyComparisons(StatsCoreSummaryDto core)
    {
        _courseComparisonTopCourseIds = core.CourseComparison.TopCourseIds;
        _courseComparisonWeekStarts = core.CourseComparison.WeekStarts;
        _courseComparisonPerWeekPerCourse = core.CourseComparison.PerWeekPerCourse;
        _courseComparisonMaxHours = core.CourseComparison.MaxHours;
        RefreshCourseComparisonLabels();

        // Course names/icons/colors come straight from the catalog here (never from T), so the
        // balance rows need no relocalization pass.
        _courseBalanceRows = core.CourseBalance
            .Select(r => new StatsCourseBalanceCard.BalanceRow(r.Name, r.Icon, r.Color, r.TargetPercent, r.ActualPercent))
            .ToList();
    }

    /// <summary>Phase 2 result -> fields. The week labels are numeric ("dd.MM."), so nothing here
    /// is localized.</summary>
    private void ApplyNotes(StatsNotesSummaryDto notes)
    {
        _notesCorrelationWeeks = notes.CorrelationWeeks
            .Select(w => new StatsNotesCorrelationCard.WeekPair(w.Label, w.NotesCount, w.NotesPercent, w.Hours, w.HoursPercent))
            .ToList();
    }

    /// <summary>Rebuilds _courseComparisonWeeks/_courseComparisonLegend from the raw per-week-
    /// per-course hour facts the builder produced, using the CURRENT T and _allCourses - covers
    /// T.CourseFallback names for since-deleted courses.</summary>
    private void RefreshCourseComparisonLabels()
    {
        if (_courseComparisonTopCourseIds.Count == 0)
        {
            _courseComparisonWeeks = new();
            _courseComparisonLegend = new();
            return;
        }

        string ColorFor(int id) => _allCourses.FirstOrDefault(c => c.Id == id)?.Color ?? "#888888";

        _courseComparisonLegend = _courseComparisonTopCourseIds
            .Select(id => new StatsCourseComparisonChartCard.LegendEntry(CourseNameOrFallback(id), ColorFor(id)))
            .ToList();

        _courseComparisonWeeks = _courseComparisonWeekStarts.Select((ws, i) =>
        {
            var bars = _courseComparisonTopCourseIds
                .Select(id => new StatsCourseComparisonChartCard.CourseBar(
                    CourseNameOrFallback(id), ColorFor(id), _courseComparisonPerWeekPerCourse[i][id], Math.Min(100, _courseComparisonPerWeekPerCourse[i][id] / _courseComparisonMaxHours * 100)))
                .ToList();
            return new StatsCourseComparisonChartCard.WeekGroup(ws.ToString("dd.MM."), bars);
        }).ToList();
    }

    /// <summary>Phase 3 result -> the semester comparison's raw facts. The three metric rows
    /// themselves are assembled in RefreshSemesterComparisonLabels, whose titles come from T.</summary>
    private void ApplySemesterComparison(StatsSemesterComparisonDto semester)
    {
        _semesterComparisonMetrics = new();
        _semesterComparisonHasData = semester.HasData;
        if (!semester.HasData) return;

        _semesterCompCurHours = semester.CurrentHours;
        _semesterCompCurGrade = semester.CurrentGrade;
        _semesterCompCurEcts = semester.CurrentEcts;
        _semesterCompPrevHours = semester.PreviousHours;
        _semesterCompPrevGrade = semester.PreviousGrade;
        _semesterCompPrevEcts = semester.PreviousEcts;
        _semesterCompCurrentSemester = semester.CurrentSemester;
        _semesterCompMaxHoursScale = semester.MaxHoursScale;
        _semesterCompMaxEctsScale = semester.MaxEctsScale;
        RefreshSemesterComparisonLabels();
    }

    /// <summary>Rebuilds _semesterComparisonMetrics/_semesterComparisonCurrentLabel from the raw
    /// hours/grade/ECTS facts the builder produced, using the CURRENT T - avoids re-running the
    /// builder on a live language switch.</summary>
    private void RefreshSemesterComparisonLabels()
    {
        if (!_semesterComparisonHasData) return;

        static string HoursLabel(double h) => StudyMetrics.FormatHoursMinutes(h);
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
}
