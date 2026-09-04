using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level lock for GET /api/metrics/summary (docs/api/metrics-contract-v1) against
/// docs/api/metrics-fixtures.json - the cross-repo pin with studylife-hacs (its own golden test
/// feeds this same file's scenarios through the coordinator's PARSING/mapping instead of
/// recomputing, so a wire-format regression here is exactly the kind of drift that test is meant
/// to catch on the other side). Unlike MetricsGoldenFixtureTests.cs (StudyLife.Shared.Tests,
/// calls StudyMetrics functions directly), this exercises the REAL HTTP endpoint: DB seeded via
/// the normal write endpoints (POST /api/sessions, PUT /api/settings, POST /api/studyprograms),
/// then GET /api/metrics/summary?now=... asserted against the same fixture numbers. Each test
/// uses its own factory/DB (UserSettingsEntity is a per-user singleton row - sharing one DB
/// across scenarios with different weekly goals/selected courses would corrupt each other).
/// </summary>
public class MetricsControllerTests
{
    [Fact]
    public async Task EmptyState_BuiltInProgram_MatchesFixtureExpected()
    {
        // "empty_state" scenario: no sessions, no courses selected/completed, default settings
        // (25-30h/week, 100-130h/month) - every metric's zero/empty-input baseline.
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/metrics/summary?now=2026-01-10T12:00:00");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MetricsSummaryDto>();
        Assert.NotNull(dto);

        Assert.True(dto!.Program.IsBuiltIn);
        Assert.Null(dto.Program.Id);
        Assert.Equal(0, dto.Streak.Current);
        Assert.Equal(0, dto.Streak.Longest);
        Assert.Equal(0.0, dto.Hours.Week);
        Assert.Equal(0.0, dto.Hours.Month);
        Assert.Equal(0.0, dto.WeekQuota.Percent);
        Assert.True(dto.WeekQuota.Warning);
        Assert.Equal(25.0, dto.WeekQuota.MissingHours);
        Assert.Equal(0.0, dto.MonthQuota.Percent);
        Assert.True(dto.MonthQuota.Warning);
        Assert.Equal(100.0, dto.MonthQuota.MissingHours);
        Assert.Null(dto.AverageGrade);
        // NOT compared against the fixture's "empty_state" forecast (unavailable there): that
        // scenario's `courses` list is itself empty, whereas this test uses the real built-in
        // catalog (180 ECTS, 6 semesters) with just no course marked completed yet - a forecast
        // IS available for that combination (CalcForecast only reports unavailable when there's
        // no remaining ECTS or no semester structure, neither of which applies here).
        Assert.True(dto.Forecast.Available);
    }

    [Fact]
    public async Task BuiltinCatalogEctsQuota_MatchesFixtureExpectedAndNewMetrics()
    {
        // "builtin_catalog_ects_quota" scenario, program=0 (built-in catalog) explicitly - its
        // `courses` in the fixture ARE the real AppliedAICourses catalog, so no custom study
        // program is needed. selectedCourseIds adds courses 6/7 (not completed) on top of the
        // completed ones, to also exercise topics/neglectedCourse (see the fixture's own
        // "$description" on this scenario's "newMetrics" block).
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var settingsResponse = await client.GetAsync("/api/settings");
        var settings = await settingsResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(settings);
        settings!.SelectedCourseIds = new List<int> { 1, 2, 3, 4, 5, 24, 25, 6, 7 };
        settings.CompletedCourseIds = new List<int> { 1, 2, 3, 4, 5, 24, 25 };
        var putResponse = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var response = await client.GetAsync("/api/metrics/summary?program=0&now=2026-01-10T12:00:00");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MetricsSummaryDto>();
        Assert.NotNull(dto);

        Assert.Equal(30, dto!.Ects.Earned);
        Assert.Equal(180, dto.Ects.Total);
        Assert.True(dto.Forecast.Available);
        Assert.Equal(new DateTime(2028, 7, 8), dto.Forecast.Date);
        Assert.Equal(0.0, dto.Forecast.RecentWeeklyHours);
        Assert.Null(dto.AverageGrade);
        Assert.Equal(0, dto.Streak.Current);

        // newMetrics block
        Assert.Empty(dto.CourseHours);
        Assert.Equal(0, dto.Topics.Completed);
        Assert.Equal(45, dto.Topics.Total);
        Assert.NotNull(dto.NeglectedCourse);
        Assert.Equal(6, dto.NeglectedCourse!.CourseId);
        Assert.Equal("Mathematik: Lineare Algebra", dto.NeglectedCourse.CourseName);
        Assert.Null(dto.NeglectedCourse.LastStudied);
        Assert.Empty(dto.UpcomingCourseGoals);
        Assert.Equal(0.0, dto.MonthComparison.CurrentMonthHours);
        Assert.Equal(0.0, dto.MonthComparison.PreviousMonthHours);
        Assert.False(dto.MonthComparison.HasYearData);
        Assert.Equal("2026-W01", dto.WeeklyReport.WeekId);
        Assert.Equal(0.0, dto.WeeklyReport.Hours);
        Assert.Null(dto.WeeklyReport.TopCourseName);
        Assert.Equal(0, dto.WeeklyReport.SessionCount);
    }

    [Fact]
    public async Task ForecastNormalPace_CustomProgram_MatchesFixtureExpectedAndNewMetrics()
    {
        // "forecast_normal_pace" scenario: a 2-course custom study program (90 ECTS each,
        // semesters 2/4, no elective groups), one 220h session on course 1 (isCompleted=true),
        // course 1 completed. Custom-course ids are shifted (StudyProgramCatalog.
        // CustomCourseIdOffset) - fetched back via GET /api/courses instead of hardcoded, so this
        // test doesn't depend on the exact assigned id.
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = "Forecast Normal Pace",
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 2, Name = "C1", Ects = 90 },
                new() { Semester = 4, Name = "C2", Ects = 90 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var program = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(program?.Id);

        var coursesResponse = await client.GetAsync($"/api/courses?program={program!.Id}");
        var courses = await coursesResponse.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        var c1 = courses!.Single(c => c.Name == "C1");
        var c2 = courses.Single(c => c.Name == "C2");

        var settingsResponse = await client.GetAsync("/api/settings");
        var settings = await settingsResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(settings);
        settings!.ActiveStudyProgramId = program.Id;
        settings.SelectedCourseIds = new List<int> { c1.Id, c2.Id };
        settings.CompletedCourseIds = new List<int> { c1.Id };
        var putResponse = await client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // The fixture's single 220h session (2026-02-19T00:00..2026-02-28T04:00) can't be created
        // through the real endpoint as one row - SessionsController.Validate caps a single
        // session at 24h. Split into 10x 22h sessions on the SAME start date instead (no overlap
        // check exists) - same total hours (220), same calendar day (still within February, still
        // within the last-completed week 2026-02-16..02-23), same forecast pace (recentWeeklyHours
        // only depends on the total, not the session count) - only SessionCount differs (10, not 1).
        for (var i = 0; i < 10; i++)
        {
            var sessionResponse = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = c1.Id,
                CourseName = "C1",
                CourseColor = "#111111",
                StartTime = new DateTime(2026, 2, 19, 0, 0, 0),
                EndTime = new DateTime(2026, 2, 19, 22, 0, 0),
                IsCompleted = true,
            });
            Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        }

        var response = await client.GetAsync("/api/metrics/summary?now=2026-03-01T12:00:00");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MetricsSummaryDto>();
        Assert.NotNull(dto);

        Assert.False(dto!.Program.IsBuiltIn);
        Assert.Equal(program.Id, dto.Program.Id);
        Assert.Equal(0, dto.Streak.Current);
        Assert.Equal(1, dto.Streak.Longest);
        Assert.Equal(90, dto.Ects.Earned);
        Assert.Equal(180, dto.Ects.Total);
        Assert.True(dto.Forecast.Available);
        Assert.Equal(new DateTime(2027, 2, 28), dto.Forecast.Date);
        Assert.Equal(27.5, dto.Forecast.RecentWeeklyHours, precision: 6);

        // newMetrics block
        var courseHours = Assert.Single(dto.CourseHours);
        Assert.Equal(c1.Id, courseHours.CourseId);
        Assert.Equal("C1", courseHours.CourseName);
        Assert.Equal("#6C5CE7", courseHours.CourseColor); // catalog default (CreateStudyProgramCourseDto.Color not set) - CourseHours reports the CATALOG course's color, not the session's own CourseColor field
        Assert.Equal(220.0, courseHours.Hours, precision: 6);
        Assert.Equal(10, courseHours.SessionCount);

        Assert.Equal(0, dto.Topics.Completed);
        Assert.Equal(0, dto.Topics.Total);
        Assert.Null(dto.NeglectedCourse); // only 1 active (non-completed) selected course - below the 2+-courses gate
        Assert.Empty(dto.UpcomingCourseGoals);

        Assert.Equal(0.0, dto.MonthComparison.CurrentMonthHours);
        Assert.Equal(220.0, dto.MonthComparison.PreviousMonthHours, precision: 6);
        Assert.Equal(-220.0, dto.MonthComparison.DeltaVsPreviousMonth, precision: 6);
        Assert.False(dto.MonthComparison.HasYearData);

        Assert.Equal("2026-W08", dto.WeeklyReport.WeekId);
        Assert.Equal(220.0, dto.WeeklyReport.Hours, precision: 6);
        Assert.Equal(220.0, dto.WeeklyReport.DeltaVsPreviousWeek, precision: 6);
        Assert.Equal("C1", dto.WeeklyReport.TopCourseName);
        Assert.Equal(10, dto.WeeklyReport.SessionCount);
    }

    [Fact]
    public async Task WeekAndMonthHours_StudiedOnly_QuotaTilesStayPlanned()
    {
        // "week_start_monday_and_studied_semantics" scenario, endpoint-level: a completed
        // session and one still scheduled for later today, both in the current week/month.
        // Hours.Week/Month must count only the completed one (StudyMetrics.IsStudied), while
        // WeekQuota/MonthQuota's own Hours field keeps counting both - see
        // docs/ARCHITECTURE.md "Number semantics".
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var studied = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "Artificial Intelligence",
            CourseColor = "#111111",
            StartTime = new DateTime(2026, 1, 5, 9, 0, 0),
            EndTime = new DateTime(2026, 1, 5, 11, 0, 0),
            IsCompleted = true,
        });
        Assert.Equal(HttpStatusCode.OK, studied.StatusCode);

        var plannedLater = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "Artificial Intelligence",
            CourseColor = "#111111",
            StartTime = new DateTime(2026, 1, 8, 20, 0, 0),
            EndTime = new DateTime(2026, 1, 8, 21, 0, 0),
            IsCompleted = false,
        });
        Assert.Equal(HttpStatusCode.OK, plannedLater.StatusCode);

        var response = await client.GetAsync("/api/metrics/summary?now=2026-01-08T18:00:00");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MetricsSummaryDto>();
        Assert.NotNull(dto);

        Assert.Equal(2.0, dto!.Hours.Week, precision: 6); // only the 09-11 studied session
        Assert.Equal(3.0, dto.WeekQuota.Hours, precision: 6); // both sessions scheduled this week
        Assert.Equal(2.0, dto.Hours.Month, precision: 6);
        Assert.Equal(3.0, dto.MonthQuota.Hours, precision: 6);
    }

    [Fact]
    public async Task NonExistentProgram_Returns404()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/metrics/summary?program=987654");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Achievements_ReturnsAll44TiersInCatalogOrder()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/metrics/achievements");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MetricsAchievementsDto>();
        Assert.NotNull(dto);
        Assert.Equal(44, dto!.Tiers.Count);
        Assert.Equal(0, dto.Unlocked); // fresh DB, nothing studied yet
        Assert.Equal(44, dto.Total);
        Assert.Equal(AchievementCatalog.HoursKey, dto.Tiers[0].Category);
        Assert.Equal(AchievementCatalog.ProgramsKey, dto.Tiers[^1].Category);
    }
}
