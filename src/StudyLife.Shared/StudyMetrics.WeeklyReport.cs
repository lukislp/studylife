namespace StudyLife.Shared;

public static partial class StudyMetrics
{
    /// <summary>Result of <see cref="CalcLastCompletedWeekReport"/>.</summary>
    public readonly record struct WeeklyReportResult(
        string WeekId,
        double Hours,
        double DeltaVsPreviousWeek,
        string? TopCourseName,
        int SessionCount);

    /// <summary>
    /// Weekly report for the last COMPLETED Mon-Sun week (never the currently in-progress one,
    /// which by definition isn't over yet) - the metrics-API/Home-Assistant variant of the
    /// weekly recap, always available regardless of what day "now" happens to be (unlike the
    /// server's own push notification, BackgroundTaskService.Reports's RunWeeklyReportAsync,
    /// which reports on the CURRENT week and only fires once, on Sunday evening - that one stays
    /// as-is, see its comment). <paramref name="studiedHistory"/> is expected to already be
    /// "studied" (StudyMetrics.IsStudied) - same precondition as RunWeeklyReportAsync's own
    /// `studied` query.
    /// </summary>
    public static WeeklyReportResult CalcLastCompletedWeekReport(IEnumerable<StudySessionDto> studiedHistory, DateTime now)
    {
        var history = studiedHistory as IReadOnlyList<StudySessionDto> ?? studiedHistory.ToList();
        var today = now.Date;
        var currentWeekStart = WeekStartOf(today);
        var weekStart = currentWeekStart.AddDays(-7);
        var weekEnd = currentWeekStart;
        var priorWeekStart = weekStart.AddDays(-7);

        var thisWeek = history.Where(s => s.StartTime.Date >= weekStart && s.StartTime.Date < weekEnd).ToList();
        var hours = thisWeek.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var priorHours = history
            .Where(s => s.StartTime.Date >= priorWeekStart && s.StartTime.Date < weekStart)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        string? topCourse = null;
        if (thisWeek.Count > 0)
        {
            topCourse = thisWeek
                .GroupBy(s => s.CourseName)
                .OrderByDescending(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours))
                .First().Key;
        }

        var weekId = $"{System.Globalization.ISOWeek.GetYear(weekStart)}-W{System.Globalization.ISOWeek.GetWeekOfYear(weekStart):D2}";
        return new WeeklyReportResult(weekId, hours, hours - priorHours, topCourse, thisWeek.Count);
    }
}
