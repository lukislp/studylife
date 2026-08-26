using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsGradeChartCard.GradePoint> _gradePoints = new();
    // Semester number behind each _gradePoints entry's Label (T.SemesterShortFormat), parallel
    // list in the same order, so RefreshGradePointLabels can rebuild the labels on a live
    // language switch without re-grouping `goals`.
    private List<int> _gradePointSemesters = new();
    private List<StatsHoursGradeScatterCard.ScatterPoint> _scatterPoints = new();
    private string _scatterMaxHoursLabel = "0h";
    private List<StatsGradeDistributionCard.GradeBucket> _gradeDistribution = new();
    private List<StatsGradeTimelineCard.GradePoint> _gradeTimelinePoints = new();
    private List<StatsHoursEctsScatterCard.ScatterPoint> _hoursEctsPoints = new();
    private string _hoursEctsMaxHoursLabel = "0h";
    private string _hoursEctsMaxEctsLabel = "0";

    private void BuildGradeDistribution(List<CourseGoalDto> goals)
    {
        // Half-grade bands of the German scale (1.0 = best grade), "> 4.0" = failed.
        // Unlike the grade history (BuildGradeHistory), deliberately WITHOUT a catalog join: the
        // distribution counts every grade given, even without an assignable semester.
        var buckets = new (string Label, decimal UpTo)[]
        {
            ("1,0–1,5", 1.5m), ("1,6–2,0", 2.0m), ("2,1–2,5", 2.5m),
            ("2,6–3,0", 3.0m), ("3,1–3,5", 3.5m), ("3,6–4,0", 4.0m), ("> 4,0", decimal.MaxValue),
        };
        var counts = new int[buckets.Length];
        foreach (var g in goals)
        {
            if (!g.Grade.HasValue) continue;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (g.Grade.Value <= buckets[i].UpTo) { counts[i]++; break; }
            }
        }
        var max = Math.Max(1, counts.Max());
        _gradeDistribution = buckets
            .Select((b, i) => new StatsGradeDistributionCard.GradeBucket(b.Label, counts[i], counts[i] / (double)max * 100))
            .ToList();
    }

    private void BuildGradeHistory(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Same weighting semantics as the average grade above (Grade × Ects / sum of Ects, falling
        // back to an unweighted mean when the Ects sum is 0), just grouped per semester. Graded
        // courses without a catalog entry drop out here (no semester can be assigned).
        var groups = goals
            .Where(g => g.Grade.HasValue)
            .Select(g => (Grade: g.Grade!.Value, Course: allCourses.FirstOrDefault(c => c.Id == g.CourseId)))
            .Where(x => x.Course != null)
            .GroupBy(x => x.Course!.Semester)
            .OrderBy(g => g.Key)
            .ToList();

        _gradePointSemesters = groups.Select(g => g.Key).ToList();
        _gradePoints = groups
            .Select(g =>
            {
                // Group is never empty (GroupBy) -> CalcWeightedAverageGrade never returns null here.
                var avg = StudyMetrics.CalcWeightedAverageGrade(g.Select(x => new StudyMetrics.GradedCourse(x.Grade, x.Course!.Ects)))!.Value;
                // German grading scale: 1.0 = best grade -> inverted, so better grades yield taller bars.
                var percent = Math.Clamp((5.0 - (double)avg) / 4.0 * 100, 0, 100);
                return new StatsGradeChartCard.GradePoint(string.Format(T.SemesterShortFormat ?? "", g.Key), avg, g.Count(), percent);
            })
            .ToList();
    }

    /// <summary>Rebuilds each _gradePoints entry's Label (T.SemesterShortFormat) from
    /// _gradePointSemesters + the CURRENT T, using the already-computed grade/count/percent -
    /// avoids re-grouping `goals` on a live language switch.</summary>
    private void RefreshGradePointLabels()
    {
        if (_gradePoints.Count != _gradePointSemesters.Count) return;
        _gradePoints = _gradePoints
            .Select((p, i) => p with { Label = string.Format(T.SemesterShortFormat ?? "", _gradePointSemesters[i]) })
            .ToList();
    }

    private void BuildGradeTimeline(List<CourseGoalDto> goals, List<CourseDto> allCourses)
    {
        // Individual grades in chronological order by actual completion date - deliberately
        // WITHOUT BuildGradeHistory's semester grouping (average per catalog semester there) and
        // without requiring a catalog entry: a grade here only needs a CompletedAt, no assignable semester.
        _gradeTimelinePoints = goals
            .Where(g => g.Grade.HasValue && g.CompletedAt.HasValue)
            .OrderBy(g => g.CompletedAt!.Value)
            .Select(g => new StatsGradeTimelineCard.GradePoint(
                g.CompletedAt!.Value,
                g.CourseName,
                allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Color ?? "#888888",
                g.Grade!.Value,
                // Inverted scale like BuildGradeHistory: better grades yield taller columns.
                Math.Clamp((5.0 - (double)g.Grade!.Value) / 4.0 * 100, 0, 100)))
            .ToList();
    }

    /// <summary>Record struct instead of a value tuple for BuildHoursEctsScatter's materialized
    /// `points` list below - LINQ over a List<(...)> of value tuples has triggered a Mono AOT
    /// crash at compile time (not call time) in the native app shell (studylife-app,
    /// BlazorWebView) that links this same Client project - see
    /// project_studylife_app_ios_aot_linq_tuple_crash.</summary>
    private readonly record struct HoursEctsPoint(CourseDto Course, double Hours, int EctsEarned);

    private void BuildHoursEctsScatter(List<CourseDto> allCourses, List<CourseHoursRow> perCourseHours, UserSettings settings)
    {
        // "What did the time actually earn?": hours per course (same raw aggregate as the
        // hours-vs-grade scatter) against the ECTS harvested. Course ECTS are all-or-nothing,
        // so ongoing courses sit at y=0 (invested, nothing harvested yet) and completed courses
        // without logged hours sit at x=0 - both deliberately visible.
        var candidates = perCourseHours.Select(r => r.Course)
            .Concat(settings.CompletedCourseIds
                .Select(id => allCourses.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Select(c => c!))
            .DistinctBy(c => c.Id)
            .ToList();

        var points = candidates
            .Select(c => new HoursEctsPoint(
                c,
                perCourseHours.FirstOrDefault(r => r.Course.Id == c.Id).Hours,
                settings.CompletedCourseIds.Contains(c.Id) ? c.Ects : 0))
            .ToList();

        var maxHours = Math.Max(1.0, points.Count == 0 ? 0 : Math.Ceiling(points.Max(p => p.Hours)));
        var maxEcts = Math.Max(1, points.Count == 0 ? 0 : points.Max(p => p.Course.Ects));
        _hoursEctsMaxHoursLabel = $"{(int)maxHours}h";
        _hoursEctsMaxEctsLabel = maxEcts.ToString();

        // 5% margin on both axes like the hours-vs-grade scatter, so edge points
        // (x=0, y=0) aren't clipped at the card border.
        _hoursEctsPoints = points
            .Select(p => new StatsHoursEctsScatterCard.ScatterPoint(
                p.Course.Name, p.Course.Icon, p.Course.Color, p.Hours, p.EctsEarned,
                5 + Math.Clamp(p.Hours / maxHours, 0, 1) * 90,
                5 + Math.Clamp(p.EctsEarned / (double)maxEcts, 0, 1) * 90))
            .ToList();
    }

    /// <summary>Record struct instead of a value tuple for BuildHoursGradeScatter's materialized
    /// `points` list below - see HoursEctsPoint's doc comment above for why.</summary>
    private readonly record struct HoursGradePoint(CourseDto Course, decimal Grade, double Hours);

    private void BuildHoursGradeScatter(List<CourseGoalDto> goals, List<CourseDto> allCourses, List<CourseHoursRow> perCourseHours)
    {
        // "Does more studying pay off?": hours per course (same source as _courseRows, the
        // `raw` aggregate from the AppStateService sessions) against the grade achieved. Graded
        // courses without logged hours deliberately appear at x=0 instead of disappearing.
        var points = goals
            .Where(g => g.Grade.HasValue)
            .Select(g => (Grade: g.Grade!.Value, Course: allCourses.FirstOrDefault(c => c.Id == g.CourseId)))
            .Where(x => x.Course != null)
            .Select(x => new HoursGradePoint(x.Course!, x.Grade, perCourseHours.FirstOrDefault(r => r.Course.Id == x.Course!.Id).Hours))
            .ToList();

        var maxHours = Math.Max(1.0, points.Count == 0 ? 0 : Math.Ceiling(points.Max(p => p.Hours)));
        _scatterMaxHoursLabel = $"{(int)maxHours}h";

        // 5% margin on both axes, so edge points (x=0, grade 1.0/5.0) aren't clipped at the card border.
        // Y inverted like the grade history: (5.0 − grade) / 4.0, better grades sit higher.
        _scatterPoints = points
            .Select(p => new StatsHoursGradeScatterCard.ScatterPoint(
                p.Course.Name, p.Course.Icon, p.Course.Color, p.Hours, p.Grade,
                5 + Math.Clamp(p.Hours / maxHours, 0, 1) * 90,
                5 + Math.Clamp((5.0 - (double)p.Grade) / 4.0, 0, 1) * 90))
            .ToList();
    }
}
