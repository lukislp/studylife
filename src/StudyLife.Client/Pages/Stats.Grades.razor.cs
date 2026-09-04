using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsGradeChartCard.GradePoint> _gradePoints = new();
    // Semester number behind each _gradePoints entry's Label (T.SemesterShortFormat), parallel
    // list in the same order, so RefreshGradePointLabels can rebuild the labels on a live
    // language switch.
    private List<int> _gradePointSemesters = new();
    private List<StatsHoursGradeScatterCard.ScatterPoint> _scatterPoints = new();
    private string _scatterMaxHoursLabel = "0h";
    private List<StatsGradeDistributionCard.GradeBucket> _gradeDistribution = new();
    private List<StatsGradeTimelineCard.GradePoint> _gradeTimelinePoints = new();
    private List<StatsHoursEctsScatterCard.ScatterPoint> _hoursEctsPoints = new();
    private string _hoursEctsMaxHoursLabel = "0h";
    private string _hoursEctsMaxEctsLabel = "0";

    /// <summary>Grade distribution/history/timeline plus the two scatter charts. The per-semester
    /// grade history is the only one that needs T (its "S{0}" label, see
    /// RefreshGradePointLabels); the grade-band labels are fixed notation of the German scale and
    /// the scatter points carry catalog names, so both come ready-made from the builder.</summary>
    private void ApplyGrades(StatsCoreSummaryDto core)
    {
        _gradeDistribution = core.GradeDistribution
            .Select(b => new StatsGradeDistributionCard.GradeBucket(b.Label, b.Count, b.Percent))
            .ToList();

        _gradePointSemesters = core.GradeHistory.Select(g => g.Semester).ToList();
        _gradePoints = core.GradeHistory
            .Select(g => new StatsGradeChartCard.GradePoint(
                string.Format(T.SemesterShortFormat ?? "", g.Semester), g.AvgGrade, g.CourseCount, g.BarPercent))
            .ToList();

        _gradeTimelinePoints = core.GradeTimeline
            .Select(p => new StatsGradeTimelineCard.GradePoint(p.Date, p.CourseName, p.Color, p.Grade, p.BarPercent))
            .ToList();

        _scatterMaxHoursLabel = core.HoursGradeScatter.MaxHoursLabel;
        _scatterPoints = core.HoursGradeScatter.Points
            .Select(p => new StatsHoursGradeScatterCard.ScatterPoint(
                p.Name, p.Icon, p.Color, p.Hours, p.Grade, p.XPercent, p.YPercent))
            .ToList();

        _hoursEctsMaxHoursLabel = core.HoursEctsScatter.MaxHoursLabel;
        _hoursEctsMaxEctsLabel = core.HoursEctsScatter.MaxEctsLabel;
        _hoursEctsPoints = core.HoursEctsScatter.Points
            .Select(p => new StatsHoursEctsScatterCard.ScatterPoint(
                p.Name, p.Icon, p.Color, p.Hours, p.EctsEarned, p.XPercent, p.YPercent))
            .ToList();
    }

    /// <summary>Rebuilds each _gradePoints entry's Label (T.SemesterShortFormat) from
    /// _gradePointSemesters + the CURRENT T, using the already-computed grade/count/percent -
    /// avoids re-running the builder on a live language switch.</summary>
    private void RefreshGradePointLabels()
    {
        if (_gradePoints.Count != _gradePointSemesters.Count) return;
        _gradePoints = _gradePoints
            .Select((p, i) => p with { Label = string.Format(T.SemesterShortFormat ?? "", _gradePointSemesters[i]) })
            .ToList();
    }
}
