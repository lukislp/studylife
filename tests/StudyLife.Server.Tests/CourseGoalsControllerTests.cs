using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share one factory/DB. CourseGoalEntity has a unique index on CourseId
/// (see StudyLifeDb.OnModelCreating), so PUT is an upsert per CourseId - every test
/// therefore uses its own CourseId, so parallel/subsequent tests don't overwrite each other.
/// Every CourseId used here must be a REAL built-in catalog id (audit finding M2: CourseId is
/// now validated against the user's full course universe, see CourseResolver) - CourseName in
/// the request DTO is deliberately IGNORED and derived server-side from the resolved course
/// instead, so assertions compare against <see cref="ExpectedCourseName"/>, not against
/// whatever the test happened to put in the DTO.
/// </summary>
public class CourseGoalsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CourseGoalsControllerTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    /// <summary>The name a valid write actually persists - the built-in catalog's own name for
    /// courseId, since CourseGoalsController.Save now derives CourseName server-side instead of
    /// trusting the client-supplied value.</summary>
    private static string ExpectedCourseName(int courseId) =>
        CourseCatalog.AppliedAICourses.First(c => c.Id == courseId).Name;

    private static CourseGoalDto ValidGoal(
        int courseId,
        decimal? grade = null,
        DateTime? targetDate = null,
        string completedTopics = "",
        string? tag = null,
        string? completionNote = null,
        DateTime? completedAt = null) => new()
        {
            CourseId = courseId,
            // Deliberately an arbitrary, wrong-looking name: the server must ignore this and
            // derive the real one from the catalog (see the class doc comment) - this is
            // exactly the "client-supplied junk ignored" case audit finding M2 requires.
            CourseName = "Client-Supplied-Junk-Name",
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
        var dto = ValidGoal(1, grade: 2.3m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/1", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.NotNull(created);
        Assert.Equal(1, created!.CourseId);
        Assert.Equal(ExpectedCourseName(1), created.CourseName);
        Assert.Equal(2.3m, created.Grade);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.Contains(all!, g => g.CourseId == 1 && g.CourseName == ExpectedCourseName(1));
    }

    [Fact]
    public async Task Save_NewCourseGoal_CustomCourse_CreatesAndReturnsDto()
    {
        // Audit finding M2: the same catalog-derivation must also work for a CUSTOM course
        // (the user's own study program, not the built-in catalog) - see CourseResolver, which
        // resolves both id ranges against the current user's full course universe.
        var (courseId, courseName) = await CustomCourseTestHelper.CreateAsync(_client);
        var dto = ValidGoal(courseId, grade: 1.7m);

        var response = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.NotNull(created);
        Assert.Equal(courseName, created!.CourseName);
    }

    [Fact]
    public async Task Save_UnknownCourseId_ReturnsBadRequestWithStableMessage()
    {
        var dto = ValidGoal(987654);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/987654", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("987654", body);
    }

    [Fact]
    public async Task Save_ExistingCourseGoal_UpdatesInPlaceRatherThanDuplicating_ButKeepsCourseNameFrozen()
    {
        await _client.PutAsJsonAsync("/api/coursegoals/2", ValidGoal(2, grade: 3.0m));

        // Second PUT for the SAME CourseId is an UPDATE, not a fresh binding - per audit finding
        // M2's frozen-at-creation semantics, CourseName is NOT re-derived on every write (a
        // later catalog rename must not silently rewrite an already-frozen row), so it stays
        // exactly what was resolved at creation regardless of what the client sends now.
        var updateResponse = await _client.PutAsJsonAsync("/api/coursegoals/2", ValidGoal(2, grade: 1.7m));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal(ExpectedCourseName(2), updated!.CourseName);
        Assert.Equal(1.7m, updated.Grade);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        // Upsert must not create a second entry for the same CourseId (unique index).
        var matching = all!.Where(g => g.CourseId == 2).ToList();
        Assert.Single(matching);
        Assert.Equal(ExpectedCourseName(2), matching[0].CourseName);
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Save_EmptyCourseName_ReturnsBadRequest()
    {
        var dto = ValidGoal(3);
        dto.CourseName = "   ";

        var response = await _client.PutAsJsonAsync("/api/coursegoals/3", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeBelowMinimum_ReturnsBadRequest()
    {
        var dto = ValidGoal(4, grade: 0.9m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/4", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeAboveMaximum_ReturnsBadRequest()
    {
        var dto = ValidGoal(5, grade: 5.1m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/5", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_GradeAtLowerBoundary_Succeeds()
    {
        var dto = ValidGoal(6, grade: 1.0m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/6", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal(1.0m, result!.Grade);
    }

    [Fact]
    public async Task Save_GradeAtUpperBoundary_Succeeds()
    {
        var dto = ValidGoal(7, grade: 5.0m);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/7", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CourseGoalDto>();
        Assert.Equal(5.0m, result!.Grade);
    }

    [Fact]
    public async Task Save_GradeNull_SucceedsForCoursesNotYetGraded()
    {
        var dto = ValidGoal(8, grade: null);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/8", dto);

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
            9,
            grade: 1.3m,
            targetDate: target,
            completedTopics: "Topic A,Topic B,Topic C",
            tag: "Pflicht",
            completionNote: "Note: sehr gut",
            completedAt: completedAt);

        var response = await _client.PutAsJsonAsync("/api/coursegoals/9", dto);

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
        var persisted = Assert.Single(all!, g => g.CourseId == 9);
        Assert.Equal("Topic A,Topic B,Topic C", persisted.CompletedTopics);
        Assert.Equal("Pflicht", persisted.Tag);
    }

    [Fact]
    public async Task Save_WithoutTargetDateOrTag_LeavesThemNull()
    {
        var dto = ValidGoal(10, targetDate: null, tag: null);
        dto.TargetDate = null;

        var response = await _client.PutAsJsonAsync("/api/coursegoals/10", dto);

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
        await _client.PutAsJsonAsync("/api/coursegoals/11", ValidGoal(11));
        await _client.PutAsJsonAsync("/api/coursegoals/12", ValidGoal(12));

        var response = await _client.GetAsync("/api/coursegoals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = await response.Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.NotNull(all);
        Assert.Contains(all!, g => g.CourseId == 11 && g.CourseName == ExpectedCourseName(11));
        Assert.Contains(all!, g => g.CourseId == 12 && g.CourseName == ExpectedCourseName(12));
    }

    // ---------- DELETE /api/coursegoals/{courseId} ----------

    [Fact]
    public async Task Delete_ExistingGoal_RemovesItFromSubsequentGet()
    {
        await _client.PutAsJsonAsync("/api/coursegoals/13", ValidGoal(13));

        var deleteResponse = await _client.DeleteAsync("/api/coursegoals/13");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var all = await (await _client.GetAsync("/api/coursegoals")).Content.ReadFromJsonAsync<List<CourseGoalDto>>();
        Assert.DoesNotContain(all!, g => g.CourseId == 13);
    }

    [Fact]
    public async Task Delete_NonExistentGoal_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/coursegoals/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
