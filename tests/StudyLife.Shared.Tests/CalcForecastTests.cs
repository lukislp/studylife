using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcForecastTests
{
    private static readonly DateTime Now = new(2026, 7, 13, 12, 0, 0);

    private static CourseDto Course(int semester) => new() { Semester = semester };

    private static StudySessionDto Session(DateTime start, double hours, bool completed = true) =>
        new() { StartTime = start, EndTime = start.AddHours(hours), IsCompleted = completed };

    [Fact]
    public void RemainingEctsZeroOrNegative_ReturnsAlreadyDone_WhenProgramHasEcts()
    {
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 180,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        Assert.False(result.Available);
        Assert.True(result.AlreadyDone);
        Assert.Null(result.ForecastDate);
    }

    [Fact]
    public void RemainingEctsNegative_StillReportsAlreadyDone()
    {
        // Earned more than the total (e.g. re-scoped catalog) - still treated as done, not an error.
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 200,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        Assert.False(result.Available);
        Assert.True(result.AlreadyDone);
    }

    [Fact]
    public void ZeroEctsTotalAndEarned_ReportsNotAlreadyDone_NotAvailable()
    {
        // remainingEcts = 0 <= 0, but ectsTotal is 0 so AlreadyDone should be false (nothing to "finish").
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 0, ectsEarned: 0,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        Assert.False(result.Available);
        Assert.False(result.AlreadyDone);
        Assert.Null(result.ForecastDate);
    }

    [Fact]
    public void EmptyCourseCatalog_NoSemesterStructure_ReturnsUnavailable()
    {
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 0,
            allCourses: Array.Empty<CourseDto>(),
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        Assert.False(result.Available);
        Assert.False(result.AlreadyDone);
        Assert.Null(result.ForecastDate);
    }

    [Fact]
    public void NoRecentHistory_DefaultsPaceRatioToOne_UsingBaselineDirectly()
    {
        // remaining=90, totalSemesters=6, ectsPerSemester=30 -> baseline = 90/30*26 = 78 weeks.
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        Assert.True(result.Available);
        Assert.False(result.AlreadyDone);
        Assert.Equal(78.0, result.BaselineWeeksNeeded, precision: 6);
        Assert.Equal(0.0, result.RecentWeeklyHours, precision: 6);
        Assert.Equal(27.5, result.ReferenceWeeklyHours, precision: 6);
        Assert.Equal(Now.Date.AddDays(78 * 7), result.ForecastDate);
    }

    [Fact]
    public void RecentPaceFasterThanGoal_ShrinksForecastByPaceRatio()
    {
        // reference = (20+20)/2 = 20. One 240h session in-window -> recentWeeklyHours = 240/8 = 30.
        // paceRatio = 30/20 = 1.5 (within [0.25, 3.0], not clamped).
        var history = new[] { Session(Now.AddDays(-10), 240) };

        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 20, weeklyGoalMaxHours: 20,
            history: history, now: Now);

        Assert.True(result.Available);
        Assert.Equal(78.0, result.BaselineWeeksNeeded, precision: 6);
        Assert.Equal(30.0, result.RecentWeeklyHours, precision: 6);
        Assert.Equal(20.0, result.ReferenceWeeklyHours, precision: 6);
        var expectedWeeksNeeded = 78.0 / 1.5;
        Assert.Equal(Now.Date.AddDays(expectedWeeksNeeded * 7), result.ForecastDate);
    }

    [Fact]
    public void PaceRatio_ClampsAtLowerBoundQuarter()
    {
        // Tiny recent activity relative to a large weekly goal drives the raw ratio far below 0.25.
        var history = new[] { Session(Now.AddDays(-5), 0.01) };

        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 30, weeklyGoalMaxHours: 30,
            history: history, now: Now);

        var expectedWeeksNeeded = result.BaselineWeeksNeeded / 0.25;
        Assert.Equal(Now.Date.AddDays(expectedWeeksNeeded * 7), result.ForecastDate);
    }

    [Fact]
    public void PaceRatio_ClampsAtUpperBoundThree()
    {
        // Huge recent activity relative to a tiny weekly goal drives the raw ratio far above 3.0.
        var history = new[] { Session(Now.AddDays(-5), 500) };

        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 1, weeklyGoalMaxHours: 1,
            history: history, now: Now);

        var expectedWeeksNeeded = result.BaselineWeeksNeeded / 3.0;
        Assert.Equal(Now.Date.AddDays(expectedWeeksNeeded * 7), result.ForecastDate);
    }

    [Fact]
    public void HistoryOlderThanEightWeeks_IsExcludedFromRecentHours()
    {
        // 8 weeks = 56 days; a session 60 days back falls outside the recent-cutoff window.
        var history = new[] { Session(Now.AddDays(-60), 100) };

        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: history, now: Now);

        Assert.Equal(0.0, result.RecentWeeklyHours, precision: 6);
    }

    [Fact]
    public void UnstudiedFutureSession_IsExcludedFromRecentHours()
    {
        // Not completed and end time is still in the future relative to `now` -> IsStudied is false.
        var history = new[] { Session(Now.AddHours(-1), 5, completed: false) };

        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(6) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: history, now: Now);

        Assert.Equal(0.0, result.RecentWeeklyHours, precision: 6);
    }

    [Fact]
    public void TotalSemesters_UsesMaxSemesterAcrossCatalog_NotCount()
    {
        // Two courses but max semester is 3 -> totalSemesters = 3, not 2.
        var result = StudyMetrics.CalcForecast(
            ectsTotal: 180, ectsEarned: 90,
            allCourses: new[] { Course(1), Course(3) },
            weeklyGoalMinHours: 25, weeklyGoalMaxHours: 30,
            history: Array.Empty<StudySessionDto>(), now: Now);

        // ectsPerSemester = 180/3 = 60; baseline = 90/60*26 = 39.
        Assert.Equal(39.0, result.BaselineWeeksNeeded, precision: 6);
    }
}
