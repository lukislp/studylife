using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share one factory/DB - that's unproblematic here because GET /api/courses is
/// purely read-only and the only mutation (POST /api/studyprograms) exclusively creates new
/// study programs uniquely named per test, instead of modifying existing state.
/// </summary>
public class CoursesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CoursesControllerTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task GetAll_NoQueryParam_ReturnsBuiltInCatalog()
    {
        var response = await _client.GetAsync("/api/courses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var courses = await response.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        Assert.Equal(58, courses!.Count);
        Assert.Contains(courses, c => c is { Id: 1, Name: "Artificial Intelligence" });
    }

    [Fact]
    public async Task GetAll_ProgramZero_ReturnsBuiltInCatalogExplicitly()
    {
        var response = await _client.GetAsync("/api/courses?program=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var courses = await response.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        Assert.Equal(58, courses!.Count);
    }

    [Fact]
    public async Task GetAll_NonExistentProgramId_FallsBackToBuiltInCatalog()
    {
        // The controller validates the Id against the DB and defensively falls back to the
        // built-in catalog for an unknown Id, instead of returning 404 or an empty list.
        var response = await _client.GetAsync("/api/courses?program=987654");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var courses = await response.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        Assert.Equal(58, courses!.Count);
    }

    [Fact]
    public async Task GetAll_NegativeProgramId_FallsBackToBuiltInCatalog()
    {
        var response = await _client.GetAsync("/api/courses?program=-5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var courses = await response.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        Assert.Equal(58, courses!.Count);
    }

    [Fact]
    public async Task GetAll_CustomProgramId_ReturnsOnlyItsOwnCoursesWithOffsetIds()
    {
        var createRequest = new CreateStudyProgramRequestDto
        {
            Name = $"Custom Program {Guid.NewGuid():N}",
            Groups = new List<CreateStudyProgramGroupDto>
            {
                new() { Name = "Wahlfach A", EctsQuota = 5 },
            },
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Kurs Eins", Code = "K1", Ects = 5, Group = null, Topics = new List<string> { "Thema A", "Thema B" } },
                new() { Semester = 2, Name = "Kurs Zwei", Code = "K2", Ects = 7, Group = "Wahlfach A", Topics = new List<string>() },
            },
        };
        var createResponse = await _client.PostAsJsonAsync("/api/studyprograms", createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);

        var response = await _client.GetAsync($"/api/courses?program={created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var courses = await response.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        Assert.Equal(2, courses!.Count);

        // Custom course ids are shifted by StudyProgramCatalog.CustomCourseIdOffset (100000)
        // to avoid colliding with the built-in catalog (ids 1-58).
        Assert.All(courses, c => Assert.True(c.Id >= 100000));

        var kurs1 = courses.Single(c => c.Name == "Kurs Eins");
        Assert.Equal(5, kurs1.Ects);
        Assert.Null(kurs1.Group);
        Assert.Equal(new List<string> { "Thema A", "Thema B" }, kurs1.Topics);
        Assert.Equal(1, kurs1.Semester);

        var kurs2 = courses.Single(c => c.Name == "Kurs Zwei");
        Assert.Equal(7, kurs2.Ects);
        Assert.Equal("Wahlfach A", kurs2.Group);
        Assert.Equal(2, kurs2.Semester);
    }

    [Fact]
    public async Task GetAll_TwoDifferentCustomPrograms_AreIsolatedFromEachOther()
    {
        var programA = await CreateProgram($"Program A {Guid.NewGuid():N}", "Kurs A-1");
        var programB = await CreateProgram($"Program B {Guid.NewGuid():N}", "Kurs B-1");

        var coursesA = await (await _client.GetAsync($"/api/courses?program={programA.Id}"))
            .Content.ReadFromJsonAsync<List<CourseDto>>();
        var coursesB = await (await _client.GetAsync($"/api/courses?program={programB.Id}"))
            .Content.ReadFromJsonAsync<List<CourseDto>>();

        Assert.Single(coursesA!);
        Assert.Single(coursesB!);
        Assert.Equal("Kurs A-1", coursesA!.Single().Name);
        Assert.Equal("Kurs B-1", coursesB!.Single().Name);
        Assert.NotEqual(coursesA!.Single().Id, coursesB!.Single().Id);
    }

    private async Task<StudyProgramSummaryDto> CreateProgram(string name, string courseName)
    {
        var request = new CreateStudyProgramRequestDto
        {
            Name = name,
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = courseName, Ects = 5 },
            },
        };
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(dto);
        return dto!;
    }
}
