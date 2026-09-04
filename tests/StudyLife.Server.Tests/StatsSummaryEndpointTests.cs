using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level parity lock for GET /api/stats/summary - same shape as
/// DashboardSummaryEndpointTests: seed a realistic dataset (including a second study programme so
/// the cross-programme comparison's ProgramCatalogs input is actually exercised) through the
/// normal write endpoints, fetch the same raw endpoints Stats.razor.cs's LoadDataAsync/
/// LoadProgramCatalogsAsync would through the SAME HttpClient, run StatsSummaryBuilder.Build
/// locally with that input, and compare the serialized result against the endpoint's own response.
/// </summary>
public class StatsSummaryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Summary_WithSecondProgram_MatchesInputAssembledFromTheSameEndpoints()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var courseIds = new List<int> { 1, 2, 3, 4, 5 };

        await SeedRealisticDatasetAsync(client, now, courseIds);

        // A second study programme (kept inactive - the built-in catalog stays the active scope)
        // so StudyPrograms.Count >= 2 and StatsSummaryBuilder.BuildProgramComparison actually
        // produces rows, exercising the ProgramCatalogs input this endpoint has to source
        // server-side in one go instead of the client's per-programme N+1 fan-out.
        var createResponse = await client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = "Stats Parity Program",
            Groups = new List<CreateStudyProgramGroupDto> { new() { Name = "Electives", EctsQuota = 10 } },
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "C1", Ects = 8, Topics = new List<string> { "T1", "T2" } },
                new() { Semester = 1, Name = "C2", Ects = 6, Group = "Electives" },
                new() { Semester = 2, Name = "C3", Ects = 12 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var program = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(program?.Id);

        var customCoursesResponse = await client.GetAsync($"/api/courses?program={program!.Id}");
        var customCourses = await customCoursesResponse.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(customCourses);

        // A few sessions logged against the second programme's own courses - membership in a
        // programme's catalog is what BuildProgramComparison filters history by, independent of
        // SelectedCourseIds (that only affects the ACTIVE programme's own course-list rows).
        foreach (var (course, i) in customCourses!.Select((c, i) => (c, i)))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = now.Date.AddDays(-(i + 1) * 2).AddHours(14),
                EndTime = now.Date.AddDays(-(i + 1) * 2).AddHours(16),
                IsCompleted = true,
            })).StatusCode);
        }

        var expected = await BuildExpectedAsync(client, now);
        var actual = await FetchSummaryAsync(client, now);
        AssertJsonEqual(expected, actual);
    }

    [Fact]
    public async Task Summary_SecondCallWithinTtl_ServedFromCache_WithEtagAnd304()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var nowQuery = NowQuery(now);

        var first = await client.GetAsync($"/api/stats/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.GetAsync($"/api/stats/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/stats/summary?now={nowQuery}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var third = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, third.StatusCode);
    }

    [Fact]
    public async Task SessionWrite_ChangesTheResultAndTheEtag()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var nowQuery = NowQuery(now);

        var before = await client.GetAsync($"/api/stats/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var etagBefore = before.Headers.ETag!.ToString();

        var created = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "x",
            CourseColor = "#000000",
            StartTime = now.Date.AddHours(8),
            EndTime = now.Date.AddHours(10),
            IsCompleted = true,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var after = await client.GetAsync($"/api/stats/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etagBefore, after.Headers.ETag!.ToString());

        var dto = await after.Content.ReadFromJsonAsync<StatsSummaryDto>();
        Assert.Contains(dto!.Core.CourseRows, r => r.Course.Id == 1 && r.SessionCount > 0);
    }

    [Fact]
    public async Task MissingNow_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stats/summary")).StatusCode);
    }

    [Fact]
    public async Task NowTooFarFromServerClock_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var farOff = NowQuery(DateTime.Now.AddDays(3));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/stats/summary?now={farOff}")).StatusCode);
    }

    [Fact]
    public async Task ApiKeyCredential_IsRejected_SessionOnlyEndpoint()
    {
        using var factory = new CustomWebApplicationFactory();
        var sessionClient = factory.CreateClient();

        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(factory, generated!.ApiKey);
        var nowQuery = NowQuery(DateTime.Now);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await keyClient.GetAsync($"/api/stats/summary?now={nowQuery}")).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// ~25+ sessions spanning past/today/this week/several months/older than both 371 and 3650
    /// days, completed and not, plus graded/open goals and notes - same shape as
    /// DashboardSummaryEndpointTests' seed, reused here so both endpoints' window arithmetic is
    /// exercised identically. courseIds must already be selected in settings by the caller
    /// (the built-in catalog's default selection already covers 1-5, so no PUT is needed here).
    /// </summary>
    private static async Task SeedRealisticDatasetAsync(HttpClient client, DateTime now, List<int> courseIds)
    {
        var sessions = new List<StudySessionDto>();
        void Add(int courseId, DateTime start, DateTime end, bool completed) => sessions.Add(new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "seed",
            CourseColor = "#123456",
            StartTime = start,
            EndTime = end,
            IsCompleted = completed,
        });

        Add(courseIds[0], now.AddHours(-1), now.AddHours(1), completed: false);
        Add(courseIds[1 % courseIds.Count], now.Date.AddHours(7), now.Date.AddHours(8), completed: true);

        for (var daysAgo = 1; daysAgo <= 5; daysAgo++)
            Add(courseIds[daysAgo % courseIds.Count], now.Date.AddDays(-daysAgo).AddHours(9), now.Date.AddDays(-daysAgo).AddHours(10), completed: true);

        for (var week = 1; week <= 8; week++)
            Add(courseIds[week % courseIds.Count], now.Date.AddDays(-7 * week).AddHours(10), now.Date.AddDays(-7 * week).AddHours(12), completed: true);

        // Inside the 371-day history window, older than the 12-week trend range.
        Add(courseIds[1 % courseIds.Count], now.Date.AddDays(-120).AddHours(9), now.Date.AddDays(-120).AddHours(11), completed: true);
        Add(courseIds[2 % courseIds.Count], now.Date.AddDays(-250).AddHours(9), now.Date.AddDays(-250).AddHours(10), completed: true);

        // Beyond the 371-day window, still inside the 3650-day semester-comparison window.
        Add(courseIds[3 % courseIds.Count], now.Date.AddDays(-450).AddHours(9), now.Date.AddDays(-450).AddHours(11), completed: true);
        Add(courseIds[4 % courseIds.Count], now.Date.AddDays(-900).AddHours(11), now.Date.AddDays(-900).AddHours(12), completed: true);
        Add(courseIds[1 % courseIds.Count], now.Date.AddDays(-2000).AddHours(10), now.Date.AddDays(-2000).AddHours(13), completed: true);

        // Beyond even the 3650-day window - excluded from both History and HeavyHistory.
        Add(courseIds[2 % courseIds.Count], now.Date.AddDays(-4000).AddHours(9), now.Date.AddDays(-4000).AddHours(10), completed: true);

        for (var i = 1; i <= 6; i++)
            Add(courseIds[i % courseIds.Count], now.Date.AddDays(-i * 3).AddHours(15), now.Date.AddDays(-i * 3).AddHours(16), completed: i % 2 == 0);

        foreach (var session in sessions)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", session)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[0]}", new CourseGoalDto
        {
            CourseId = courseIds[0],
            CourseName = "x",
            Grade = 1.3m,
            CompletedTopics = "",
            CompletedAt = now.AddDays(-30),
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[1 % courseIds.Count]}", new CourseGoalDto
        {
            CourseId = courseIds[1 % courseIds.Count],
            CourseName = "x",
            TargetDate = now.AddDays(20),
            CompletedTopics = "",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[2 % courseIds.Count]}", new CourseGoalDto
        {
            CourseId = courseIds[2 % courseIds.Count],
            CourseName = "x",
            Grade = 2.7m,
            CompletedTopics = "",
            CompletedAt = now.AddDays(-5),
        })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Lecture recap",
            Content = "Key points from today's lecture.",
            CourseId = courseIds[0],
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "General todo",
            Content = "Buy a new notebook.",
        })).StatusCode);
    }

    /// <summary>
    /// Assembles StatsSummaryInput exactly like Stats.razor.cs's LoadDataAsync/
    /// LoadProgramCatalogsAsync does - fetching the same endpoints through the SAME HttpClient the
    /// seed data was written through - and runs the shared builder locally. This is the
    /// "expected" side of the parity assertion.
    /// </summary>
    private static async Task<StatsSummaryDto> BuildExpectedAsync(HttpClient client, DateTime now)
    {
        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        var allCourses = await client.GetFromJsonAsync<List<CourseDto>>("/api/courses");
        var sessions = await client.GetFromJsonAsync<List<StudySessionDto>>("/api/sessions");
        var history = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={StatsSummaryBuilder.HistoryDays}");
        var heavyHistory = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={StatsSummaryBuilder.AllTimeHistoryDays}");
        var goals = await client.GetFromJsonAsync<List<CourseGoalDto>>("/api/coursegoals");
        var studyPrograms = await client.GetFromJsonAsync<List<StudyProgramSummaryDto>>("/api/studyprograms");
        var notes = await client.GetFromJsonAsync<List<NoteDto>>("/api/notes");

        IReadOnlyDictionary<string, int> groupQuotas = CourseCatalog.GroupEctsQuotas;
        if (settings!.ActiveStudyProgramId is int activeProgramId)
        {
            var detail = await client.GetFromJsonAsync<StudyProgramDetailDto>($"/api/studyprograms/{activeProgramId}");
            groupQuotas = detail!.GroupEctsQuotas;
        }

        // Same LoadProgramCatalogsAsync gate as the client: only fan out once there are at least
        // two programmes, and skip the detail fetch for the built-in one (Id null).
        var programCatalogs = new List<StatsProgramCatalogDto>();
        if (studyPrograms!.Count >= 2)
        {
            foreach (var program in studyPrograms)
            {
                var courses = await client.GetFromJsonAsync<List<CourseDto>>($"/api/courses?program={program.Id ?? 0}");
                var quotas = new Dictionary<string, int>();
                if (program.Id is int programId)
                {
                    var detail = await client.GetFromJsonAsync<StudyProgramDetailDto>($"/api/studyprograms/{programId}");
                    quotas = detail!.GroupEctsQuotas;
                }
                programCatalogs.Add(new StatsProgramCatalogDto { ProgramId = program.Id, Courses = courses!, GroupQuotas = quotas });
            }
        }

        var input = new StatsSummaryInput
        {
            Settings = settings,
            AllCourses = allCourses!,
            Sessions = sessions!,
            History = history!,
            HeavyHistory = heavyHistory!,
            Goals = goals!,
            GroupQuotas = groupQuotas,
            StudyPrograms = studyPrograms,
            ProgramCatalogs = programCatalogs,
            Notes = notes!,
            Now = now,
        };
        return StatsSummaryBuilder.Build(input);
    }

    private static async Task<StatsSummaryDto> FetchSummaryAsync(HttpClient client, DateTime now)
    {
        var response = await client.GetAsync($"/api/stats/summary?now={NowQuery(now)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<StatsSummaryDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Unspecified);

    private static string NowQuery(DateTime now) => Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ss"));

    private static void AssertJsonEqual(StatsSummaryDto expected, StatsSummaryDto actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
}
