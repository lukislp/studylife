namespace StudyLife.Shared;

/// <summary>
/// Builds the printable study report (see <see cref="ReportSummaryDto"/>) from raw inputs.
/// Extracted verbatim from Report.razor.cs's OnTextLoadedAsync, so client and server compute
/// identical numbers - same LINQ, same rounding, same ordering, same tie-breaking.
/// </summary>
public static class ReportSummaryBuilder
{
    /// <summary>Lookback of the full history fetch - a study record must show the ENTIRE study
    /// time to date, not the ±7/90-day window the dashboard/stats tiles use.</summary>
    public const int HistoryDays = 3650;

    public static ReportSummaryDto Build(ReportSummaryInput input)
    {
        var settings = input.Settings;
        var allCourses = input.AllCourses;
        var activeCourseIds = allCourses.Select(c => c.Id).ToHashSet();
        var today = input.Now.Date;

        var goals = input.Goals.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();
        var history = input.History.Where(s => activeCourseIds.Contains(s.CourseId)).ToList();

        var programmeName = input.StudyPrograms.FirstOrDefault(p => p.Id == settings.ActiveStudyProgramId)?.Name
            ?? CourseCatalog.BuiltInProgramName;

        // Same StudyMetrics.CalcCourseHours call as Stats.razor.cs (selected + completed +
        // courses that actually have sessions).
        var raw = StudyMetrics.CalcCourseHours(
            allCourses, settings.SelectedCourseIds, settings.CompletedCourseIds, history, input.Now);

        // Sorted by semester instead of by hours (as on the stats page) - for a record, the
        // chronological order of the study progression is the more natural reading.
        var courseRows = raw
            .OrderBy(r => r.Course.Semester).ThenByDescending(r => r.Hours)
            .Select(r =>
            {
                var goal = goals.FirstOrDefault(g => g.CourseId == r.Course.Id);
                int? daysRemaining = goal?.TargetDate.HasValue == true
                    ? (goal!.TargetDate!.Value.Date - today).Days
                    : null;
                var isCompleted = settings.CompletedCourseIds.Contains(r.Course.Id);
                return new ReportCourseRowDto
                {
                    Course = r.Course,
                    Hours = r.Hours,
                    SessionCount = r.SessionCount,
                    IsCompleted = isCompleted,
                    DaysRemaining = daysRemaining,
                    CompletionNote = goal?.CompletionNote,
                    Grade = goal?.Grade,
                };
            })
            .ToList();

        var totalSessions = courseRows.Sum(r => r.SessionCount);
        var totalHours = courseRows.Sum(r => r.Hours);
        var totalHoursLabel = $"{(int)totalHours}h {(int)((totalHours - (int)totalHours) * 60)}m";

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, allCourses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));
        var averageGradeLabel = averageGrade.HasValue ? StudyMetrics.FormatGrade(averageGrade.Value) : null;

        var ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, input.GroupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, input.GroupQuotas);
        var ectsPercent = ectsTotal > 0 ? Math.Min(100.0, ectsEarned / (double)ectsTotal * 100) : 0;

        DateTime? periodStart = null;
        DateTime? periodEnd = null;
        if (history.Count > 0)
        {
            periodStart = history.Min(s => s.StartTime.Date);
            periodEnd = history.Max(s => s.StartTime.Date);
        }

        return new ReportSummaryDto
        {
            CourseRows = courseRows,
            TotalSessions = totalSessions,
            TotalHoursLabel = totalHoursLabel,
            AverageGradeLabel = averageGradeLabel,
            EctsEarned = ectsEarned,
            EctsTotal = ectsTotal,
            EctsPercent = ectsPercent,
            ProgrammeName = programmeName,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
        };
    }
}
