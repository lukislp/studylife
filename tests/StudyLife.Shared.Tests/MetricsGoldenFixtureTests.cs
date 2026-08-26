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
        MetricsExpected Expected);

    private record MetricsSettings(
        int WeeklyGoalMinHours, int WeeklyGoalMaxHours,
        int MonthlyGoalMinHours, int MonthlyGoalMaxHours);

    private record MetricsCourseGoal(
        int CourseId, string? CourseName, decimal? Grade,
        DateTime? TargetDate, DateTime? CompletedAt, string? CompletedTopics);

    private record MetricsExpected(
        int Streak, int LongestStreak,
        double WeekHours, double WeekQuotaPercent, bool WeekQuotaWarning, double WeekQuotaMissingHours,
        double MonthHours, double MonthQuotaPercent, bool MonthQuotaWarning, double MonthQuotaMissingHours,
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

        var weekQuota = StudyMetrics.CalcQuota(weekHours, s.Settings.WeeklyGoalMinHours, s.Settings.WeeklyGoalMaxHours);
        var monthQuota = StudyMetrics.CalcQuota(monthHours, s.Settings.MonthlyGoalMinHours, s.Settings.MonthlyGoalMaxHours);

        var gradedCourses = s.CourseGoals
            .Where(g => g.Grade.HasValue)
            .Select(g => (g.Grade!.Value, s.Courses.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5))
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
        Assert.Equal(expected.WeekQuotaPercent, weekQuota.Percent, precision: 6);
        Assert.Equal(expected.WeekQuotaWarning, weekQuota.Warning);
        Assert.Equal(expected.WeekQuotaMissingHours, weekQuota.MissingHours, precision: 6);

        Assert.Equal(expected.MonthHours, monthHours, precision: 6);
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
