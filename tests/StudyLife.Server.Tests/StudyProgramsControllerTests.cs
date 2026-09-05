using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Own class, because this test needs a DB without any previously created study program -
/// the POST tests below in <see cref="StudyProgramsControllerTests"/> would otherwise
/// pollute the list depending on xUnit's execution order.
/// </summary>
public class StudyProgramsControllerFreshDbTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public StudyProgramsControllerFreshDbTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task GetAll_OnFreshDatabase_ReturnsOnlySyntheticBuiltInEntry()
    {
        var response = await _client.GetAsync("/api/studyprograms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var programs = await response.Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.NotNull(programs);
        var entry = Assert.Single(programs!);
        Assert.Null(entry.Id);
        Assert.True(entry.IsBuiltIn);
        Assert.False(entry.IsCompleted);
        Assert.Equal("Applied Artificial Intelligence", entry.Name);
    }
}

/// <summary>
/// Tests share one factory/DB. Every mutating test creates its own, uniquely named study
/// program instead of modifying existing state, so that the execution order within the
/// class doesn't matter.
/// </summary>
public class StudyProgramsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public StudyProgramsControllerTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    private static CreateStudyProgramRequestDto MinimalValidRequest(string? name = null) => new()
    {
        Name = name ?? $"Programm {Guid.NewGuid():N}",
        Courses = new List<CreateStudyProgramCourseDto>
        {
            new() { Semester = 1, Name = "Grundlagenkurs", Ects = 5 },
        },
    };

    // ---- Create: happy path ----------------------------------------------------------

    [Fact]
    public async Task Create_ValidProgramWithGroupsAndCourses_AppearsInListAndDetailShowsQuotas()
    {
        var name = $"Informatik {Guid.NewGuid():N}";
        var request = new CreateStudyProgramRequestDto
        {
            Name = name,
            Groups = new List<CreateStudyProgramGroupDto>
            {
                new() { Name = "Wahlpflicht X", EctsQuota = 15 },
                new() { Name = "Wahlpflicht Y", EctsQuota = 20 },
            },
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Pflichtkurs", Ects = 5 },
                new() { Semester = 2, Name = "Wahlkurs 1", Ects = 8, Group = "Wahlpflicht X" },
                new() { Semester = 3, Name = "Wahlkurs 2", Ects = 12, Group = "Wahlpflicht Y" },
            },
        };

        var createResponse = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);
        Assert.NotNull(created!.Id);
        Assert.Equal(name, created.Name);
        Assert.False(created.IsBuiltIn);
        Assert.False(created.IsCompleted);

        var listResponse = await _client.GetAsync("/api/studyprograms");
        var list = await listResponse.Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.Contains(list!, p => p.Id == created.Id && p.Name == name);

        var detailResponse = await _client.GetAsync($"/api/studyprograms/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<StudyProgramDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(created.Id, detail!.Id);
        Assert.Equal(name, detail.Name);
        Assert.Equal(2, detail.GroupEctsQuotas.Count);
        Assert.Equal(15, detail.GroupEctsQuotas["Wahlpflicht X"]);
        Assert.Equal(20, detail.GroupEctsQuotas["Wahlpflicht Y"]);
    }

    [Fact]
    public async Task Create_CourseWithoutGroup_HasNullGroupInCatalog()
    {
        var request = MinimalValidRequest();
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", request))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var courses = await (await _client.GetAsync($"/api/courses?program={created!.Id}"))
            .Content.ReadFromJsonAsync<List<CourseDto>>();
        var course = Assert.Single(courses!);
        Assert.Null(course.Group);
    }

    [Fact]
    public async Task Create_CourseWithBlankColorAndIcon_FallsBackToDefaults()
    {
        var request = MinimalValidRequest();
        request.Courses[0].Color = "";
        request.Courses[0].Icon = "   ";
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", request))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var courses = await (await _client.GetAsync($"/api/courses?program={created!.Id}"))
            .Content.ReadFromJsonAsync<List<CourseDto>>();
        var course = Assert.Single(courses!);
        Assert.Equal("#6C5CE7", course.Color);
        Assert.Equal("📚", course.Icon);
    }

    [Fact]
    public async Task Create_TopicsWithEmbeddedCommasAndBlankEntries_AreStrippedAndFiltered()
    {
        var request = MinimalValidRequest();
        request.Courses[0].Topics = new List<string> { "A, B", " C ", "", "   " };
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", request))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var courses = await (await _client.GetAsync($"/api/courses?program={created!.Id}"))
            .Content.ReadFromJsonAsync<List<CourseDto>>();
        var course = Assert.Single(courses!);
        // Commas are removed from topic names (storage format is comma-separated), empty
        // entries filtered out after trimming.
        Assert.Equal(new List<string> { "A B", "C" }, course.Topics);
    }

    // ---- Create: validation -----------------------------------------------------------

    [Fact]
    public async Task Create_EmptyName_ReturnsBadRequest()
    {
        var request = MinimalValidRequest("");
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WhitespaceOnlyName_ReturnsBadRequest()
    {
        var request = MinimalValidRequest("   ");
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NameTooLong_ReturnsBadRequest()
    {
        var request = MinimalValidRequest(new string('a', 101));
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NameAtMaxLength_IsAccepted()
    {
        var request = MinimalValidRequest(new string('a', 100));
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_ZeroCourses_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Courses = new List<CreateStudyProgramCourseDto>();
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_TooManyCourses_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Courses = Enumerable.Range(1, 301)
            .Select(i => new CreateStudyProgramCourseDto { Semester = 1, Name = $"Kurs {i}", Ects = 5 })
            .ToList();
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_TooManyGroups_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Groups = Enumerable.Range(1, 51)
            .Select(i => new CreateStudyProgramGroupDto { Name = $"Gruppe {i}", EctsQuota = 5 })
            .ToList();
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateGroupNamesCaseInsensitive_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Groups = new List<CreateStudyProgramGroupDto>
        {
            new() { Name = "Wahlpflicht A", EctsQuota = 5 },
            new() { Name = "wahlpflicht a", EctsQuota = 10 },
        };
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_GroupWithEmptyName_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Groups = new List<CreateStudyProgramGroupDto> { new() { Name = "  ", EctsQuota = 5 } };
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_GroupWithNonPositiveEctsQuota_ReturnsBadRequest(int quota)
    {
        var request = MinimalValidRequest();
        request.Groups = new List<CreateStudyProgramGroupDto> { new() { Name = "Gruppe", EctsQuota = quota } };
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_CourseWithEmptyName_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Courses[0].Name = "   ";
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Create_CourseWithNonPositiveEcts_ReturnsBadRequest(int ects)
    {
        var request = MinimalValidRequest();
        request.Courses[0].Ects = ects;
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task Create_CourseWithSemesterOutOfRange_ReturnsBadRequest(int semester)
    {
        var request = MinimalValidRequest();
        request.Courses[0].Semester = semester;
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Create_CourseWithSemesterAtBoundary_IsAccepted(int semester)
    {
        var request = MinimalValidRequest();
        request.Courses[0].Semester = semester;
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_CourseReferencingNonExistentGroup_ReturnsBadRequest()
    {
        var request = MinimalValidRequest();
        request.Courses[0].Group = "Gibt es nicht";
        var response = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Completed flag -----------------------------------------------------------------

    [Fact]
    public async Task SetCompleted_TogglesFlag_ReflectedInSubsequentGet()
    {
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var listBefore = await (await _client.GetAsync("/api/studyprograms"))
            .Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.False(listBefore!.Single(p => p.Id == created!.Id).IsCompleted);

        var setTrue = await _client.PutAsJsonAsync($"/api/studyprograms/{created!.Id}/completed", new SetStudyProgramCompletedDto { IsCompleted = true });
        Assert.Equal(HttpStatusCode.OK, setTrue.StatusCode);
        var afterTrueBody = await setTrue.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.True(afterTrueBody!.IsCompleted);

        var listAfterTrue = await (await _client.GetAsync("/api/studyprograms"))
            .Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.True(listAfterTrue!.Single(p => p.Id == created.Id).IsCompleted);

        // No automatism: the flag stays exactly what was last explicitly set, until the
        // next explicit PUT - here back to false.
        var setFalse = await _client.PutAsJsonAsync($"/api/studyprograms/{created.Id}/completed", new SetStudyProgramCompletedDto { IsCompleted = false });
        Assert.Equal(HttpStatusCode.OK, setFalse.StatusCode);

        var listAfterFalse = await (await _client.GetAsync("/api/studyprograms"))
            .Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.False(listAfterFalse!.Single(p => p.Id == created.Id).IsCompleted);
    }

    [Fact]
    public async Task SetCompleted_NonExistentProgram_ReturnsNotFound()
    {
        var response = await _client.PutAsJsonAsync("/api/studyprograms/999999/completed", new SetStudyProgramCompletedDto { IsCompleted = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Get detail ----------------------------------------------------------------------

    [Fact]
    public async Task Get_NonExistentProgram_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/studyprograms/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Delete --------------------------------------------------------------------------

    [Fact]
    public async Task Delete_NonExistentProgram_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/studyprograms/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_BuiltInProgramId_ReturnsNotFound()
    {
        // The built-in degree program has no DB row and therefore never a real int ID - ID 0
        // is guaranteed to never hit a real program (even with SQLite autoincrement starting at 1).
        var response = await _client.DeleteAsync("/api/studyprograms/0");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingProgram_RemovesItAndItsCourses()
    {
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var deleteResponse = await _client.DeleteAsync($"/api/studyprograms/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await _client.GetAsync("/api/studyprograms");
        var list = await listResponse.Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.DoesNotContain(list!, p => p.Id == created.Id);

        // Courses of the deleted program are gone -> controller falls back to the built-in
        // catalog (same semantics as an unknown Id).
        var coursesResponse = await _client.GetAsync($"/api/courses?program={created.Id}");
        Assert.Equal(HttpStatusCode.OK, coursesResponse.StatusCode);
        var courses = await coursesResponse.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.Equal(58, courses!.Count);

        var detailResponse = await _client.GetAsync($"/api/studyprograms/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
    }

    private static UserSettingsDto ValidSettingsWithActiveProgram(int? programId) => new()
    {
        SelectedCourseIds = new List<int> { 1, 2 },
        CompletedCourseIds = new List<int>(),
        Theme = "dark",
        WeeklyGoalMinHours = 10,
        WeeklyGoalMaxHours = 20,
        MonthlyGoalMinHours = 40,
        MonthlyGoalMaxHours = 80,
        StudyWindowStartHour = 8,
        StudyWindowEndHour = 21,
        ActiveStudyProgramId = programId,
    };

    [Fact]
    public async Task Delete_ActiveProgram_ResetsSettingsActiveStudyProgramIdToNull()
    {
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var putSettings = await _client.PutAsJsonAsync("/api/settings", ValidSettingsWithActiveProgram(created!.Id));
        Assert.Equal(HttpStatusCode.OK, putSettings.StatusCode);
        var settingsAfterActivate = await putSettings.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(created.Id, settingsAfterActivate!.ActiveStudyProgramId);

        var deleteResponse = await _client.DeleteAsync($"/api/studyprograms/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var settingsAfterDelete = await (await _client.GetAsync("/api/settings"))
            .Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Null(settingsAfterDelete!.ActiveStudyProgramId);
    }

    [Fact]
    public async Task Delete_InactiveProgram_DoesNotChangeSettingsActiveStudyProgramId()
    {
        var active = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        var other = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(active);
        Assert.NotNull(other);

        var putSettings = await _client.PutAsJsonAsync("/api/settings", ValidSettingsWithActiveProgram(active!.Id));
        Assert.Equal(HttpStatusCode.OK, putSettings.StatusCode);

        var deleteResponse = await _client.DeleteAsync($"/api/studyprograms/{other!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var settingsAfterDelete = await (await _client.GetAsync("/api/settings"))
            .Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(active.Id, settingsAfterDelete!.ActiveStudyProgramId);
    }
}

/// <summary>
/// Own factory instance (fresh DB): exercises hiding the built-in study program
/// (SettingsController.DismissBuiltInProgram) end to end, including its guard (needs a real
/// program to already exist) and the mirrored guard on StudyProgramsController.Delete once
/// dismissed (refuses to remove the user's last program) - both need a controlled program
/// count that the shared-DB StudyProgramsControllerTests above can't guarantee, and dismissal
/// is a persistent per-user flag that would otherwise leak into later tests in that class.
/// </summary>
public class DismissBuiltInProgramTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public DismissBuiltInProgramTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    private static CreateStudyProgramRequestDto MinimalValidRequest() => new()
    {
        Name = $"Programm {Guid.NewGuid():N}",
        Courses = new List<CreateStudyProgramCourseDto>
        {
            new() { Semester = 1, Name = "Grundlagenkurs", Ects = 5 },
        },
    };

    [Fact]
    public async Task DismissBuiltInProgram_FullFlow()
    {
        // Cannot hide it before any real program exists - would leave the account with
        // nothing to fall back to at all.
        var tooEarly = await _client.PostAsync("/api/settings/builtin-program/dismiss", null);
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        // A fresh program without activating it - ActiveStudyProgramId stays null (built-in
        // still active), which dismissing must reassign automatically.
        var created = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created);

        var settingsBefore = await (await _client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Null(settingsBefore!.ActiveStudyProgramId);
        Assert.False(settingsBefore.BuiltInProgramDismissed);

        var dismissResponse = await _client.PostAsync("/api/settings/builtin-program/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, dismissResponse.StatusCode);
        var dismissed = await dismissResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.True(dismissed!.BuiltInProgramDismissed);
        Assert.Equal(created!.Id, dismissed.ActiveStudyProgramId);

        // The synthetic built-in entry no longer appears in the switcher.
        var list = await (await _client.GetAsync("/api/studyprograms"))
            .Content.ReadFromJsonAsync<List<StudyProgramSummaryDto>>();
        Assert.DoesNotContain(list!, p => p.Id == null);
        Assert.Contains(list!, p => p.Id == created.Id);

        // With the built-in fallback hidden, this is now the only program - deleting it must
        // be refused instead of leaving ActiveStudyProgramId pointing at nothing.
        var deleteOnlyResponse = await _client.DeleteAsync($"/api/studyprograms/{created.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, deleteOnlyResponse.StatusCode);
        var settingsAfterRefusedDelete = await (await _client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(created.Id, settingsAfterRefusedDelete!.ActiveStudyProgramId);

        // A second program exists now - deleting the (still inactive) first one is fine again.
        var second = await (await _client.PostAsJsonAsync("/api/studyprograms", MinimalValidRequest()))
            .Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(second);
        var deleteWithSiblingResponse = await _client.DeleteAsync($"/api/studyprograms/{second!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteWithSiblingResponse.StatusCode);
    }
}
