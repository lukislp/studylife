using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// POST api/planner/exam-plan (Home Assistant service "generate_exam_plan") - untested until
/// the planner protection rule was lifted (2026-07-19). Tests within this class share
/// the DB; each test therefore sets the settings it depends on itself
/// (program selection, study window, study days) and only checks its own response.
/// </summary>
public class PlannerControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlannerControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task UseBuiltInCatalogAsync() =>
        BackgroundTaskTestSettings.PutAsync(_client, s => s.ActiveStudyProgramId = null);

    [Fact]
    public async Task ExamPlan_BuiltInCourse_CreatesSessionsBeforeExamDate()
    {
        await UseBuiltInCatalogAsync();
        var examDate = DateTime.Today.AddDays(10);

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = examDate,
            TotalHours = 3,
            SessionLengthMinutes = 90,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotNull(created);
        Assert.NotEmpty(created!);
        Assert.All(created!, s =>
        {
            Assert.True(s.Id > 0); // actually saved, not just proposed
            Assert.Equal(CourseCatalog.AppliedAICourses[0].Id, s.CourseId);
            Assert.True(s.EndTime <= examDate, "Session must end before the exam day");
            Assert.Equal(90, (s.EndTime - s.StartTime).TotalMinutes);
        });
    }

    [Fact]
    public async Task ExamPlan_PastExamDate_IsRejected()
    {
        await UseBuiltInCatalogAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = DateTime.Today,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExamPlan_UnknownCourse_IsRejected()
    {
        await UseBuiltInCatalogAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = 987654,
            ExamDate = DateTime.Today.AddDays(10),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExamPlan_CustomProgramCourse_ResolvesViaActiveProgram()
    {
        // Create a custom study program and make it active - before the
        // course-resolution extension, the endpoint responded here with BadRequest
        // ("not in catalog"), because it only knew CourseCatalog.AppliedAICourses.
        var createResponse = await _client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = "Planner-Testprogramm",
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Name = "Planner-Testkurs", Code = "PT101", Topics = new List<string> { "Thema A", "Thema B" } },
            },
        });
        createResponse.EnsureSuccessStatusCode();
        var program = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(program);
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.ActiveStudyProgramId = program!.Id);

        var courses = await _client.GetFromJsonAsync<List<CourseDto>>($"/api/courses?program={program!.Id}");
        var customCourse = Assert.Single(courses!);

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = customCourse.Id,
            ExamDate = DateTime.Today.AddDays(10),
            TotalHours = 1.5,
            SessionLengthMinutes = 90,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotEmpty(created!);
        Assert.All(created!, s => Assert.Equal("Planner-Testkurs", s.CourseName));
        // Topics come from the custom course, not from the built-in catalog.
        Assert.Contains(created!, s => s.Topic is "Thema A" or "Thema B");
    }

    [Fact]
    public async Task ExamPlan_RespectsStudyWindowAndStudyDays()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.ActiveStudyProgramId = null;
            s.StudyDays = "1"; // Monday only (DayOfWeek.Monday)
            s.StudyWindowStartHour = 9;
            s.StudyWindowEndHour = 12;
        });

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = DateTime.Today.AddDays(15), // reliably covers at least one Monday
            TotalHours = 1.5,
            SessionLengthMinutes = 90,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotEmpty(created!);
        Assert.All(created!, s =>
        {
            Assert.Equal(DayOfWeek.Monday, s.StartTime.DayOfWeek);
            Assert.True(s.StartTime.Hour >= 9);
            Assert.True(s.EndTime.Hour <= 12);
        });

        // Clean up for sibling tests in the same class (shared DB).
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.ActiveStudyProgramId = null);
    }
}
