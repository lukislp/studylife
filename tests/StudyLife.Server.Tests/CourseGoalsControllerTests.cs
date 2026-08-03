using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share one factory/DB. CourseGoalEntity has a unique index on CourseId
/// (see StudyLifeDb.OnModelCreating), so PUT is an upsert per CourseId - every test
/// therefore uses its own CourseId, so parallel/subsequent tests don't overwrite each other.
/// </summary>
public class CourseGoalsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CourseGoalsControllerTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private static CourseGoalDto ValidGoal(
        int courseId,
        string courseName = "Analysis 1",
        decimal? grade = null,
        DateTime? targetDate = null,
        string completedTopics = "",
        string? tag = null,
        string? completionNote = null,
        DateTime? completedAt = null) => new()
        {
            CourseId = courseId,
            CourseName = courseName,
            TargetDate = targetDate ?? DateTime.UtcNow.Date.AddDays(30),
            CompletionNote = completionNote,
            CompletedAt = completedAt,
            Grade = grade,
            CompletedTopics = completedTopics,
            Tag = tag,
        };

    // ---------- PUT /api/coursegoals/{courseId} (Create) ----------

    [Fact]
    public async Task Save_NewCourseGoal_CreatesAndReturnsDto()
    {
        var dto = ValidGoal(201, courseName: "Lineare Algebra", grade: 2.3m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/201", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.NotNull(created);
        Assert.Equal(201, created!.CourseId);
        Assert.Equal("Lineare Algebra", created.CourseName);
        Assert.Equal(2.3m, created.Grade);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.Contains(all!, g => g.CourseId == 201 && g.CourseName == "Lineare Algebra");
    }

    [Fact]
    public async Task Save_ExistingCourseGoal_UpdatesInPlaceRatherThanDuplicating()
    {
        await _client.PutAsJsonAsync("/api/coursegoals/211", ValidGoal(211, courseName: "Statistik", grade: 3.0m));

        var updateResponse = await _client.PutAsJsonAsync("/api/coursegoals/211", ValidGoal(211, courseName: "Statistik II", grade: 1.7m));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal("Statistik II", updated!.CourseName);
        Assert.Equal(1.7m, updated.Grade);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        // Upsert must not create a second entry for the same CourseId (unique index).
        var matching = all!.Where(g => g.CourseId == 211).ToList();
        Assert.Single(matching);
        Assert.Equal("Statistik II", matching[0].CourseName);
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Save_EmptyCourseName_ReturnsBadRequest()
    {
        var dto = ValidGoal(221, courseName: "   ");

        var response = await _client.PutAsJsonAsync("/api/coursegoals/221", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeBelowMinimum_ReturnsBadRequest()
    {
        var dto = ValidGoal(231, grade: 0.9m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/231", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeAboveMaximum_ReturnsBadRequest()
    {
        var dto = ValidGoal(232, grade: 5.1m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/232", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeAtLowerBoundary_Succeeds()
    {
        var dto = ValidGoal(233, grade: 1.0m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/233", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal(1.0m, result!.Grade);
    }

    [Fact]
    public async Task Save_GradeAtUpperBoundary_Succeeds()
    {
        var dto = ValidGoal(234, grade: 5.0m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/234", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal(5.0m, result!.Grade);
    }

    [Fact]
    public async Task Save_GradeNull_SucceedsForCoursesNotYetGraded()
    {
        var dto = ValidGoal(235, grade: null);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/235", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Null(result!.Grade);
    }

    // ---------- Field round-trip ----------

    [Fact]
    public async Task Save_PersistsCompletedTopicsTagAndDates()
    {
        var target = new DateTime(2026, 9, 15);
        var completedAt = new DateTime(2026, 8, 1);
        var dto = ValidGoal(
            241,
            courseName: "Numerik",
            grade: 1.3m,
            targetDate: target,
            completedTopics: "Topic A,Topic B,Topic C",
            tag: "Pflicht",
            completionNote: "Note: sehr gut",
            completedAt: completedAt);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/241", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.NotNull(result);
        Assert.Equal("Topic A,Topic B,Topic C", result!.CompletedTopics);
        Assert.Equal("Pflicht", result.Tag);
        Assert.Equal("Note: sehr gut", result.CompletionNote);
        Assert.Equal(target, result.TargetDate);
        Assert.Equal(completedAt, result.CompletedAt);

        // Confirm again via GET that this isn't just the PUT echo.
        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        var persisted = Assert.Single(all!, g => g.CourseId == 241);
        Assert.Equal("Topic A,Topic B,Topic C", persisted.CompletedTopics);
        Assert.Equal("Pflicht", persisted.Tag);
    }

    [Fact]
    public async Task Save_WithoutTargetDateOrTag_LeavesThemNull()
    {
        var dto = ValidGoal(242, targetDate: null, tag: null);
        dto.TargetDate = null;

        var response = await _client.PutAsJsonAsync("/api/coursegoals/242", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Null(result!.TargetDate);
        Assert.Null(result.Tag);
        Assert.Equal("", result.CompletedTopics);
    }

    // ---------- GET /api/coursegoals ----------

    [Fact]
    public async Task GetAll_ReturnsAllPersistedGoals()
    {
        await _client.PutAsJsonAsync("/api/coursegoals/251", ValidGoal(251, courseName: "Kurs A"));
        await _client.PutAsJsonAsync("/api/coursegoals/252", ValidGoal(252, courseName: "Kurs B"));

        var response = await _client.GetAsync("/api/coursegoals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = await response.Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.NotNull(all);
        Assert.Contains(all!, g => g.CourseId == 251 && g.CourseName == "Kurs A");
        Assert.Contains(all!, g => g.CourseId == 252 && g.CourseName == "Kurs B");
    }

    // ---------- DELETE /api/coursegoals/{courseId} ----------

    [Fact]
    public async Task Delete_ExistingGoal_RemovesItFromSubsequentGet()
    {
        await _client.PutAsJsonAsync("/api/coursegoals/261", ValidGoal(261));

        var deleteResponse = await _client.DeleteAsync("/api/coursegoals/261");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.DoesNotContain(all!, g => g.CourseId == 261);
    }

    [Fact]
    public async Task Delete_NonExistentGoal_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/coursegoals/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
