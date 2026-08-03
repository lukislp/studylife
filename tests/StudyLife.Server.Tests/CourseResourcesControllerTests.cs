using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share one factory/DB, so each test uses its own CourseId (same pattern as
/// CourseGoalsControllerTests) so that parallel/subsequent tests don't interfere with
/// each other.
/// </summary>
public class CourseResourcesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CourseResourcesControllerTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private static CourseResourceDto ValidResource(int courseId, string title = "Vorlesungsfolien", string url = "https://example.com/slides.pdf") => new()
    {
        CourseId = courseId,
        Title = title,
        Url = url,
    };

    // ---------- POST /api/courseresources (Create) ----------

    [Fact]
    public async Task Create_ValidResource_ReturnsCreatedWithId()
    {
        var dto = ValidResource(301, title: "Kurswebsite", url: "https://uni.example.edu/course-301");

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CourseResourceDto>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal(301, created.CourseId);
        Assert.Equal("Kurswebsite", created.Title);
        Assert.Equal("https://uni.example.edu/course-301", created.Url);
    }

    [Fact]
    public async Task Create_TrimsTitleAndUrl()
    {
        var dto = ValidResource(302, title: "  Buchlink  ", url: "  https://example.com/book  ");

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CourseResourceDto>();
        Assert.Equal("Buchlink", created!.Title);
        Assert.Equal("https://example.com/book", created.Url);
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Create_EmptyTitle_ReturnsBadRequest()
    {
        var dto = ValidResource(303, title: "   ");

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_CourseIdZeroOrNegative_ReturnsBadRequest()
    {
        var dto = ValidResource(0);

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task Create_MalformedOrNonHttpUrl_ReturnsBadRequest(string url)
    {
        var dto = ValidResource(304, url: url);

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_HttpUrl_Succeeds()
    {
        var dto = ValidResource(305, url: "http://example.com/plain-http");

        var response = await _client.PostAsJsonAsync("/api/courseresources", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- GET /api/courseresources?courseId= ----------

    [Fact]
    public async Task GetByCourse_ReturnsOnlyResourcesForThatCourse()
    {
        await _client.PostAsJsonAsync("/api/courseresources", ValidResource(311, title: "A"));
        await _client.PostAsJsonAsync("/api/courseresources", ValidResource(311, title: "B"));
        await _client.PostAsJsonAsync("/api/courseresources", ValidResource(312, title: "C - anderer Kurs"));

        var response = await _client.GetAsync("/api/courseresources?courseId=311");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<CourseResourceDto>>();
        Assert.NotNull(results);
        Assert.Equal(2, results!.Count);
        Assert.All(results, r => Assert.Equal(311, r.CourseId));
        Assert.DoesNotContain(results, r => r.Title == "C - anderer Kurs");
    }

    [Fact]
    public async Task GetByCourse_NoResources_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/courseresources?courseId=999888");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<CourseResourceDto>>();
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    /// <summary>
    /// Covers both course-Id ranges that the task explicitly requires: the built-in catalog
    /// (1-62) and custom courses (Ids starting at StudyProgramCatalog.CustomCourseIdOffset,
    /// simulated here representatively with an Id > 100000, without touching the
    /// planner-adjacent catalog file itself).
    /// </summary>
    [Fact]
    public async Task GetByCourse_WorksForBuiltInAndCustomCourseIdRanges()
    {
        const int builtInCourseId = 7;
        const int customCourseId = 100042;
        await _client.PostAsJsonAsync("/api/courseresources", ValidResource(builtInCourseId, title: "Eingebauter Kurs"));
        await _client.PostAsJsonAsync("/api/courseresources", ValidResource(customCourseId, title: "Eigener Kurs"));

        var builtInResponse = await (await _client.GetAsync($"/api/courseresources?courseId={builtInCourseId}"))
            .Content.ReadFromJsonAsync<List<CourseResourceDto>>();
        var customResponse = await (await _client.GetAsync($"/api/courseresources?courseId={customCourseId}"))
            .Content.ReadFromJsonAsync<List<CourseResourceDto>>();

        Assert.Contains(builtInResponse!, r => r.Title == "Eingebauter Kurs");
        Assert.Contains(customResponse!, r => r.Title == "Eigener Kurs");
    }

    // ---------- DELETE /api/courseresources/{id} ----------

    [Fact]
    public async Task Delete_ExistingResource_RemovesItFromSubsequentGet()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/courseresources", ValidResource(321));
        var created = await createResponse.Content.ReadFromJsonAsync<CourseResourceDto>();

        var deleteResponse = await _client.DeleteAsync($"/api/courseresources/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var remaining = await (await _client.GetAsync("/api/courseresources?courseId=321"))
            .Content.ReadFromJsonAsync<List<CourseResourceDto>>();
        Assert.DoesNotContain(remaining!, r => r.Id == created.Id);
    }

    [Fact]
    public async Task Delete_NonExistentResource_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/courseresources/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
