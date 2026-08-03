using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share one factory/DB - unlike CourseGoalEntity, there is no unique index here,
/// every test creates its own records with unique names/CourseIds so that parallel/subsequent
/// tests don't interfere with each other (especially GetAll, which returns ALL templates
/// created so far).
/// </summary>
public class SessionTemplatesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SessionTemplatesControllerTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private static SessionTemplateDto ValidTemplate(
        string name,
        int courseId = 501,
        string courseName = "Analysis 1",
        int durationMinutes = 90,
        string? topic = null,
        int? defaultWeekday = null,
        TimeSpan? defaultStartTime = null) => new()
        {
            Name = name,
            CourseId = courseId,
            CourseName = courseName,
            CourseColor = "#6C5CE7",
            DurationMinutes = durationMinutes,
            Topic = topic,
            DefaultWeekday = defaultWeekday,
            DefaultStartTime = defaultStartTime,
        };

    // ---------- POST /api/sessiontemplates (Create) ----------

    [Fact]
    public async Task Create_ValidTemplate_ReturnsCreatedDtoWithId()
    {
        var dto = ValidTemplate("Vorlesung Analysis", courseId: 601, defaultWeekday: 1, defaultStartTime: new TimeSpan(10, 0, 0));

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionTemplateDto>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("Vorlesung Analysis", created.Name);
        Assert.Equal(601, created.CourseId);
        Assert.Equal(90, created.DurationMinutes);
        Assert.Equal(1, created.DefaultWeekday);
        Assert.Equal(new TimeSpan(10, 0, 0), created.DefaultStartTime);
    }

    [Fact]
    public async Task Create_TrimsWhitespaceFromName()
    {
        var dto = ValidTemplate("  Übung Statistik  ", courseId: 602);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionTemplateDto>();
        Assert.Equal("Übung Statistik", created!.Name);
    }

    [Fact]
    public async Task Create_WithoutOptionalFields_PersistsNulls()
    {
        var dto = ValidTemplate("Minimal-Vorlage", courseId: 603, topic: null, defaultWeekday: null, defaultStartTime: null);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionTemplateDto>();
        Assert.Null(created!.Topic);
        Assert.Null(created.DefaultWeekday);
        Assert.Null(created.DefaultStartTime);
    }

    [Fact]
    public async Task Create_WithTopicAndCreatedAt_RoundTripsCorrectly()
    {
        var dto = ValidTemplate("Vorlesung mit Thema", courseId: 604, topic: "Backpropagation");

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionTemplateDto>();
        Assert.Equal("Backpropagation", created!.Topic);
        // CreatedAt is set server-side (UtcNow), not taken over from the client.
        Assert.True(created.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Create_EmptyName_ReturnsBadRequest()
    {
        var dto = ValidTemplate("   ", courseId: 611);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ZeroCourseId_ReturnsBadRequest()
    {
        var dto = ValidTemplate("Ungültiger Kurs", courseId: 0);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_EmptyCourseName_ReturnsBadRequest()
    {
        var dto = ValidTemplate("Fehlender Kursname", courseId: 612, courseName: "");
        dto.CourseName = "";

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ZeroDuration_ReturnsBadRequest()
    {
        var dto = ValidTemplate("Null-Dauer", courseId: 613, durationMinutes: 0);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NegativeDuration_ReturnsBadRequest()
    {
        var dto = ValidTemplate("Negative Dauer", courseId: 614, durationMinutes: -10);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WeekdayOutOfRange_ReturnsBadRequest()
    {
        var dto = ValidTemplate("Ungültiger Wochentag", courseId: 615, defaultWeekday: 7);

        var response = await _client.PostAsJsonAsync("/api/sessiontemplates", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WeekdayAtBoundaries_Succeeds()
    {
        var sunday = ValidTemplate("Sonntags-Vorlage", courseId: 616, defaultWeekday: 0);
        var saturday = ValidTemplate("Samstags-Vorlage", courseId: 617, defaultWeekday: 6);

        var sundayResponse = await _client.PostAsJsonAsync("/api/sessiontemplates", sunday);
        var saturdayResponse = await _client.PostAsJsonAsync("/api/sessiontemplates", saturday);

        Assert.Equal(HttpStatusCode.OK, sundayResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saturdayResponse.StatusCode);
    }

    // ---------- GET /api/sessiontemplates ----------

    [Fact]
    public async Task GetAll_ReturnsAllPersistedTemplates()
    {
        await _client.PostAsJsonAsync("/api/sessiontemplates", ValidTemplate("Vorlage A", courseId: 621));
        await _client.PostAsJsonAsync("/api/sessiontemplates", ValidTemplate("Vorlage B", courseId: 622));

        var response = await _client.GetAsync("/api/sessiontemplates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = await response.Content.ReadFromJsonAsync<List<SessionTemplateDto>>();
        Assert.NotNull(all);
        Assert.Contains(all!, t => t.Name == "Vorlage A" && t.CourseId == 621);
        Assert.Contains(all!, t => t.Name == "Vorlage B" && t.CourseId == 622);
    }

    // ---------- DELETE /api/sessiontemplates/{id} ----------

    [Fact]
    public async Task Delete_ExistingTemplate_RemovesItFromSubsequentGet()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessiontemplates", ValidTemplate("Zu löschende Vorlage", courseId: 631));
        var created = await createResponse.Content.ReadFromJsonAsync<SessionTemplateDto>();

        var deleteResponse = await _client.DeleteAsync($"/api/sessiontemplates/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var all = await (await _client.GetAsync("/api/sessiontemplates")).Content.ReadFromJsonAsync<List<SessionTemplateDto>>();
        Assert.DoesNotContain(all!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task Delete_NonExistentTemplate_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/sessiontemplates/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_TwiceInARow_SecondCallReturnsNotFound()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/sessiontemplates", ValidTemplate("Doppelt gelöscht", courseId: 632));
        var created = await createResponse.Content.ReadFromJsonAsync<SessionTemplateDto>();

        var firstDelete = await _client.DeleteAsync($"/api/sessiontemplates/{created!.Id}");
        var secondDelete = await _client.DeleteAsync($"/api/sessiontemplates/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
    }
}
