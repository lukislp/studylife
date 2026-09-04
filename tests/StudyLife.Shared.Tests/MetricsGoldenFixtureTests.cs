using System.Text.Json;
using System.Text.Json.Serialization;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Golden-fixture tests for audit finding D4: studylife-hacs's coordinator.py re-implements
/// these exact metrics (streak/longest-streak/quota/forecast/weighted-grade/ECTS) as a
/// deliberately parallel Python implementation, manually kept in sync with no CI check on
/// either side. docs/api/metrics-fixtures.json is the single source of truth for scenario
/// inputs AND expected outputs - this class is what makes that file trustworthy: it loads the
/// same fixtures and asserts they match what StudyMetrics/CourseCatalog ACTUALLY compute, so
/// the fixture can never silently drift from the real C# behavior. The Home Assistant side
/// (studylife-hacs/tests/test_metrics_golden_fixtures.py) fetches this same committed file and
/// asserts coordinator.py's own _calc_* helpers reproduce the same numbers - see that module and
/// the fixture file's "$description"/per-scenario "description" fields for two scenarios that
/// are EXPECTED to currently fail there (real, pre-existing drift between the two
/// implementations, not a bug in this test or the fixture).
/// </summary>
public class MetricsGoldenFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private record MetricsFixtureFile(List<MetricsScenario> Scenarios);

    private record MetricsScenario(
        string Name,
        string? Description,
        DateTime Now,
        MetricsSettings Settings,
        List<StudySessionDto> Sessions,
        List<CourseDto> Courses,
        List<MetricsCourseGoal> CourseGoals,
        List<int> CompletedCourseIds,
        MetricsExpected Expected,
        // Additive, only present on the handful of scenarios whose existing inputs also
        // exercise the newer centralized metrics (course hours, neglected course, topics,
        // month comparison, upcoming goals, weekly report - see metrics-contract-v1). Null on
        // every other scenario, which simply skips NewSharedMetrics_MatchExpectations below.
        List<int>? SelectedCourseIds,
        NewMetricsExpected? NewMetrics);

    private record NewMetricsExpected(
        List<MetricsCourseHoursExpected> CourseHours,
        int TopicsCompleted, int TopicsTotal,
        MetricsNeglectedCourseExpected? NeglectedCourse,
        List<MetricsUpcomingGoalExpected> UpcomingCourseGoals,
        MetricsMonthComparisonExpected MonthComparison,
        MetricsWeeklyReportExpected WeeklyReport);

    private record MetricsCourseHoursExpected(int CourseId, string CourseName, string CourseColor, double Hours, int SessionCount);
    private record MetricsNeglectedCourseExpected(int CourseId, string CourseName, DateTime? LastStudied, int? DaysSince);
    private record MetricsUpcomingGoalExpected(int CourseId, string CourseName, DateTime TargetDate, int DaysLeft);
    private record MetricsMonthComparisonExpected(double CurrentMonthHours, double PreviousMonthHours, double DeltaVsPreviousMonth, bool HasYearData, double? SameMonthLastYearHours, double? DeltaVsLastYear);
    private record MetricsWeeklyReportExpected(string WeekId, double Hours, double DeltaVsPreviousWeek, string? TopCourseName, int SessionCount);

    private record MetricsSettings(
        int WeeklyGoalMinHours, int WeeklyGoalMaxHours,
        int MonthlyGoalMinHours, int MonthlyGoalMaxHours);

    private record MetricsCourseGoal(
        int CourseId, string? CourseName, decimal? Grade,
        DateTime? TargetDate, DateTime? CompletedAt, string? CompletedTopics);

    private record MetricsExpected(
        int Streak, int LongestStreak,
        double WeekHours, double WeekStudiedHours, double WeekQuotaPercent, bool WeekQuotaWarning, double WeekQuotaMissingHours,
        double MonthHours, double MonthStudiedHours, double MonthQuotaPercent, bool MonthQuotaWarning, double MonthQuotaMissingHours,
        decimal? AverageGrade, int EctsEarned, int EctsTotal,
        bool ForecastAvailable, DateTime? ForecastDate, double? ForecastRecentWeeklyHours);

    /// <summary>Walks up from the test binary's directory to find the repo root (marked by
    /// StudyLife.sln) - the fixture file is committed at docs/api/metrics-fixtures.json
    /// relative to that root, not to wherever `dotnet test` happens to place the build output.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException(
                $"Could not find StudyLife.sln above {AppContext.BaseDirectory} - can't locate the repo root to load docs/api/metrics-fixtures.json.");
        return dir.FullName;
    }

    private static readonly Lazy<MetricsFixtureFile> Fixture = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "api", "metrics-fixtures.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MetricsFixtureFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"{path} deserialized to null.");
    });

    public static IEnumerable<object[]> Scenarios() =>
        Fixture.Value.Scenarios.Select(s => new object[] { s.Name });

    private static MetricsScenario Find(string name) =>
        Fixture.Value.Scenarios.Single(s => s.Name == name);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void CoordinatorMetrics_MatchStudyMetricsAndCourseCatalog(string scenarioName)
    {
        var s = Find(scenarioName);
        var now = s.Now;
        var today = now.Date;
        var expected = s.Expected;

        var weekStart = StudyMetrics.WeekStartOf(today);
        var weekEnd = weekStart.AddDays(7);
        // Mirrors Index.razor.cs's week-sessions filter (bounded to [weekStart, weekEnd)) -
        // this bound lives as plain LINQ at every call site, not inside StudyMetrics itself, but
        // it IS the real, live app behavior being pinned here (see the
        // week_quota_future_dated_session_drift scenario's description for why this matters).
        var weekSessions = s.Sessions.Where(x => x.StartTime.Date >= weekStart && x.StartTime.Date < weekEnd).ToList();
        var weekHours = weekSessions.Sum(x => (x.EndTime - x.StartTime).TotalHours);

        var monthStart = new DateTime(today.Year, today.Month, 1);
        // Also mirrors Index.razor.cs: deliberately NO upper bound (this one IS shared,
        // non-drifting behavior between C# and coordinator.py - both lack an upper bound here).
        var monthSessions = s.Sessions.Where(x => x.StartTime.Date >= monthStart).ToList();
        var monthHours = monthSessions.Sum(x => (x.EndTime - x.StartTime).TotalHours);

        var studied = s.Sessions.Where(x => StudyMetrics.IsStudied(x, now)).ToList();
        var streak = StudyMetrics.CalcStreak(studied.Select(x => x.StartTime), today);
        var longestStreak = StudyMetrics.CalcLongestStreak(studied.Select(x => x.StartTime));

        // MetricsHoursDto.Week/Month (unlike weekHours/monthHours above, which feed
        // WeekQuota/MonthQuota only) are STUDIED-only - same window, same studied filter
        // MetricsController.ComputeSummaryAsync applies. See docs/ARCHITECTURE.md "Number
        // semantics" and the fixture's own $fieldNotes.
        var weekStudiedHours = studied.Where(x => x.StartTime.Date >= weekStart && x.StartTime.Date < weekEnd)
            .Sum(x => (x.EndTime - x.StartTime).TotalHours);
        var monthStudiedHours = studied.Where(x => x.StartTime.Date >= monthStart)
            .Sum(x => (x.EndTime - x.StartTime).TotalHours);

        var weekQuota = StudyMetrics.CalcQuota(weekHours, s.Settings.WeeklyGoalMinHours, s.Settings.WeeklyGoalMaxHours);
        var monthQuota = StudyMetrics.CalcQuota(monthHours, s.Settings.MonthlyGoalMinHours, s.Settings.MonthlyGoalMaxHours);

        var gradedCourses = s.CourseGoals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, s.Courses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5))
            .ToList();
        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(gradedCourses);

        // Program-aware overload with an EMPTY group-quota dictionary unless the scenario is
        // specifically about the built-in catalog's static GroupEctsQuotas (that overload
        // unconditionally adds all 4 fixed quotas regardless of input - see
        // CourseCatalogTests.EmptyCourseList_StillCountsTheFourFixedGroupQuotas - so it's only
        // valid for the real AppliedAICourses catalog, never for an arbitrary/custom course list).
        //
        // "custom_program_group_quota_not_embedded_in_name" needs its own explicit quota
        // dictionary {"Electives": 5}: that's what the real app fetches from GET
        // /api/studyprograms/{id} (StudyProgramDetailDto.GroupEctsQuotas) for a custom study
        // program - a DB-stored value that never appears anywhere in the `courses` list itself
        // (fixture schema deliberately has no such field: coordinator.py has no way to obtain
        // it either, see api.py - that gap IS the drift this scenario pins, not a fixture bug).
        int ectsEarned, ectsTotal;
        if (s.Name == "builtin_catalog_ects_quota")
        {
            ectsEarned = CourseCatalog.CalcEctsEarned(s.Courses, s.CompletedCourseIds);
            ectsTotal = CourseCatalog.CalcTotalEcts(s.Courses);
        }
        else
        {
            var quotas = s.Name == "custom_program_group_quota_not_embedded_in_name"
                ? new Dictionary<string, int> { ["Electives"] = 5 }
                : new Dictionary<string, int>();
            ectsEarned = CourseCatalog.CalcEctsEarned(s.Courses, s.CompletedCourseIds, quotas);
            ectsTotal = CourseCatalog.CalcTotalEcts(s.Courses, quotas);
        }

        var forecast = StudyMetrics.CalcForecast(
            ectsTotal, ectsEarned, s.Courses,
            s.Settings.WeeklyGoalMinHours, s.Settings.WeeklyGoalMaxHours, s.Sessions, now);

        Assert.Equal(expected.Streak, streak);
        Assert.Equal(expected.LongestStreak, longestStreak);

        Assert.Equal(expected.WeekHours, weekHours, precision: 6);
        Assert.Equal(expected.WeekStudiedHours, weekStudiedHours, precision: 6);
        Assert.Equal(expected.WeekQuotaPercent, weekQuota.Percent, precision: 6);
        Assert.Equal(expected.WeekQuotaWarning, weekQuota.Warning);
        Assert.Equal(expected.WeekQuotaMissingHours, weekQuota.MissingHours, precision: 6);

        Assert.Equal(expected.MonthHours, monthHours, precision: 6);
        Assert.Equal(expected.MonthStudiedHours, monthStudiedHours, precision: 6);
        Assert.Equal(expected.MonthQuotaPercent, monthQuota.Percent, precision: 6);
        Assert.Equal(expected.MonthQuotaWarning, monthQuota.Warning);
        Assert.Equal(expected.MonthQuotaMissingHours, monthQuota.MissingHours, precision: 6);

        // Rounded to 6 decimals: StudyMetrics.CalcWeightedAverageGrade returns a raw, unrounded
        // decimal (e.g. 33.5m/15 has a repeating fraction) while the fixture stores a
        // human-legible truncated value.
        Assert.Equal(expected.AverageGrade, averageGrade.HasValue ? Math.Round(averageGrade.Value, 6) : null);

        Assert.Equal(expected.EctsEarned, ectsEarned);
        Assert.Equal(expected.EctsTotal, ectsTotal);

        Assert.Equal(expected.ForecastAvailable, forecast.Available);
        if (expected.ForecastAvailable)
        {
            Assert.Equal(expected.ForecastDate, forecast.ForecastDate);
            Assert.Equal(expected.ForecastRecentWeeklyHours!.Value, forecast.RecentWeeklyHours, precision: 6);
        }
        else
        {
            Assert.Null(expected.ForecastDate);
        }
    }

    public static IEnumerable<object[]> ScenariosWithNewMetrics() =>
        Fixture.Value.Scenarios.Where(s => s.NewMetrics != null).Select(s => new object[] { s.Name });

    /// <summary>
    /// Pins the dashboard-aggregate functions extracted for the metrics API (StudyMetrics.
    /// CalcCourseHours/CalcNeglectedCourse/CalcTopicsProgress/CalcMonthComparison/
    /// CalcUpcomingCourseGoals/CalcLastCompletedWeekReport) - same "load fixture, run the real
    /// code, compare" shape as CoordinatorMetrics_MatchStudyMetricsAndCourseCatalog above, just
    /// for the metrics that didn't exist yet when that test was written. Only scenarios whose
    /// existing inputs (courses/sessions/completedCourseIds, plus the additive selectedCourseIds
    /// where present) actually exercise these newer metrics carry a "newMetrics" block - see each
    /// scenario's own "$description" for what it pins.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScenariosWithNewMetrics))]
    public void NewSharedMetrics_MatchExpectations(string scenarioName)
    {
        var s = Find(scenarioName);
        var expected = s.NewMetrics!;
        var now = s.Now;
        var today = now.Date;
        var selectedCourseIds = s.SelectedCourseIds ?? new List<int>();
        var goals = s.CourseGoals.Select(g => new CourseGoalDto
        {
            CourseId = g.CourseId,
            CourseName = g.CourseName ?? "",
            TargetDate = g.TargetDate,
            CompletedAt = g.CompletedAt,
            Grade = g.Grade,
            CompletedTopics = g.CompletedTopics ?? "",
        }).ToList();

        var courseHours = StudyMetrics.CalcCourseHours(s.Courses, selectedCourseIds, s.CompletedCourseIds, s.Sessions, now);
        var actualCourseHours = courseHours.Select(r => new MetricsCourseHoursExpected(r.Course.Id, r.Course.Name, r.Course.Color, r.Hours, r.SessionCount)).ToList();
        Assert.Equal(expected.CourseHours.Count, actualCourseHours.Count);
        for (var i = 0; i < expected.CourseHours.Count; i++)
        {
            Assert.Equal(expected.CourseHours[i].CourseId, actualCourseHours[i].CourseId);
            Assert.Equal(expected.CourseHours[i].CourseName, actualCourseHours[i].CourseName);
            Assert.Equal(expected.CourseHours[i].CourseColor, actualCourseHours[i].CourseColor);
            Assert.Equal(expected.CourseHours[i].Hours, actualCourseHours[i].Hours, precision: 6);
            Assert.Equal(expected.CourseHours[i].SessionCount, actualCourseHours[i].SessionCount);
        }

        var topics = StudyMetrics.CalcTopicsProgress(s.Courses, selectedCourseIds, goals);
        Assert.Equal(expected.TopicsCompleted, topics.Completed);
        Assert.Equal(expected.TopicsTotal, topics.Total);

        var neglected = StudyMetrics.CalcNeglectedCourse(s.Courses, selectedCourseIds, s.CompletedCourseIds, s.Sessions.Where(x => StudyMetrics.IsStudied(x, now)), today);
        if (expected.NeglectedCourse == null)
        {
            Assert.Null(neglected);
        }
        else
        {
            Assert.NotNull(neglected);
            Assert.Equal(expected.NeglectedCourse.CourseId, neglected!.Value.Course.Id);
            Assert.Equal(expected.NeglectedCourse.CourseName, neglected.Value.Course.Name);
            Assert.Equal(expected.NeglectedCourse.LastStudied, neglected.Value.LastStudied);
        }

        var upcoming = StudyMetrics.CalcUpcomingCourseGoals(goals, today);
        Assert.Equal(expected.UpcomingCourseGoals.Count, upcoming.Count);
        for (var i = 0; i < expected.UpcomingCourseGoals.Count; i++)
        {
            Assert.Equal(expected.UpcomingCourseGoals[i].CourseId, upcoming[i].CourseId);
            Assert.Equal(expected.UpcomingCourseGoals[i].CourseName, upcoming[i].CourseName);
            Assert.Equal(expected.UpcomingCourseGoals[i].TargetDate, upcoming[i].TargetDate);
            Assert.Equal(expected.UpcomingCourseGoals[i].DaysLeft, upcoming[i].DaysLeft);
        }

        var studiedForMonth = s.Sessions.Where(x => StudyMetrics.IsStudied(x, now));
        var monthComparison = StudyMetrics.CalcMonthComparison(studiedForMonth, today);
        Assert.Equal(expected.MonthComparison.CurrentMonthHours, monthComparison.CurrentMonthHours, precision: 6);
        Assert.Equal(expected.MonthComparison.PreviousMonthHours, monthComparison.PreviousMonthHours, precision: 6);
        Assert.Equal(expected.MonthComparison.DeltaVsPreviousMonth, monthComparison.DeltaVsPreviousMonth, precision: 6);
        Assert.Equal(expected.MonthComparison.HasYearData, monthComparison.HasYearData);
        Assert.Equal(expected.MonthComparison.SameMonthLastYearHours, monthComparison.SameMonthLastYearHours);
        Assert.Equal(expected.MonthComparison.DeltaVsLastYear, monthComparison.DeltaVsLastYear);

        var studiedForWeek = s.Sessions.Where(x => StudyMetrics.IsStudied(x, now));
        var weeklyReport = StudyMetrics.CalcLastCompletedWeekReport(studiedForWeek, now);
        Assert.Equal(expected.WeeklyReport.WeekId, weeklyReport.WeekId);
        Assert.Equal(expected.WeeklyReport.Hours, weeklyReport.Hours, precision: 6);
        Assert.Equal(expected.WeeklyReport.DeltaVsPreviousWeek, weeklyReport.DeltaVsPreviousWeek, precision: 6);
        Assert.Equal(expected.WeeklyReport.TopCourseName, weeklyReport.TopCourseName);
        Assert.Equal(expected.WeeklyReport.SessionCount, weeklyReport.SessionCount);
    }

    /// <summary>Sanity check on the fixture file itself: every scenario name must be unique
    /// (MemberData/Theory would otherwise silently only run the last one under a given name),
    /// and the file must be non-empty - an accidentally-empty Scenarios list would make every
    /// [Theory] above report "0 tests ran" instead of a failure, which is worse than useless
    /// for a drift-detection suite.</summary>
    [Fact]
    public void FixtureFile_HasScenarios_WithUniqueNames()
    {
        var names = Fixture.Value.Scenarios.Select(s => s.Name).ToList();
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
