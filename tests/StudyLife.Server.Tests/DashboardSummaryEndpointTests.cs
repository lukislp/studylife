using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level parity lock for GET /api/dashboard/summary. The whole point of this endpoint is
/// that it assembles DashboardSummaryInput from the DB EXACTLY like Index.razor.cs's LoadDataAsync
/// assembles it from its own nine fetches - so the core test here seeds a realistic dataset
/// through the normal write endpoints, fetches those same nine endpoints through the SAME
/// HttpClient, runs DashboardSummaryBuilder.Build locally with that input, and compares the
/// serialized result against the endpoint's own response. A mismatch means the endpoint's
/// server-side reimplementation of "what the client would have fetched" has drifted from what the
/// real endpoints actually return - exactly the class of bug this test exists to catch.
/// </summary>
public class DashboardSummaryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Summary_BuiltInProgram_MatchesInputAssembledFromTheSameEndpoints()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var courseIds = new List<int> { 1, 2, 3, 4, 5 };

        await SeedRealisticDatasetAsync(client, now, courseIds);

        var expected = await BuildExpectedAsync(client, now);
        var actual = await FetchSummaryAsync(client, now);
        AssertJsonEqual(expected, actual);
    }

    /// <summary>Same parity claim, but for a custom study program (elective groups + custom
    /// courses) instead of the built-in catalog - GroupQuotas/AllCourses come from a different
    /// source (StudyProgramCatalog.LoadCoursesAsync/LoadGroupQuotasAsync via ProgrammeScopeResolver)
    /// on the server, GET /api/courses?program=/api/studyprograms/{id} on the client side.</summary>
    [Fact]
    public async Task Summary_CustomProgram_MatchesInputAssembledFromTheSameEndpoints()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);

        var createResponse = await client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = "Dashboard Parity Program",
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

        var coursesResponse = await client.GetAsync($"/api/courses?program={program!.Id}");
        var courses = await coursesResponse.Content.ReadFromJsonAsync<List<CourseDto>>();
        Assert.NotNull(courses);
        var courseIds = courses!.Select(c => c.Id).ToList();

        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        settings!.ActiveStudyProgramId = program.Id;
        settings.SelectedCourseIds = courseIds;
        settings.CompletedCourseIds = new List<int> { courseIds[0] };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        await SeedRealisticDatasetAsync(client, now, courseIds);

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

        var first = await client.GetAsync($"/api/dashboard/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.GetAsync($"/api/dashboard/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/dashboard/summary?now={nowQuery}");
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

        var before = await client.GetAsync($"/api/dashboard/summary?now={nowQuery}");
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

        var after = await client.GetAsync($"/api/dashboard/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etagBefore, after.Headers.ETag!.ToString());

        var dto = await after.Content.ReadFromJsonAsync<DashboardSummaryDto>();
        Assert.Contains(dto!.Sessions.TodaySessions, s => s.CourseId == 1);
    }

    [Fact]
    public async Task MissingNow_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task NowTooFarFromServerClock_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var farOff = NowQuery(DateTime.Now.AddDays(3));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/dashboard/summary?now={farOff}")).StatusCode);
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
        // A bare API key - even one from the widest slot (ha) - must NOT reach this endpoint:
        // no slot in ApiKeyScopes lists it, and SessionOnly rejects it before scoping applies.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await keyClient.GetAsync($"/api/dashboard/summary?now={nowQuery}")).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// ~25+ sessions spanning past/today/future, completed and not, deliberately reaching past
    /// both DashboardSummaryBuilder.HistoryDays (400) and AchievementHistoryDays (3650) - so the
    /// endpoint's own window arithmetic (mirroring SessionsController.GetHistory) is actually
    /// exercised, not just "everything recent". Plus a couple of course goals (grade + open
    /// target date) and two notes. courseIds must already be selected in settings by the caller.
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

        // Right now: an in-progress session and a later-today upcoming one.
        Add(courseIds[0], now.AddHours(-1), now.AddHours(1), completed: false);
        Add(courseIds[1 % courseIds.Count], now.AddHours(2), now.AddHours(3), completed: false);
        // Today, already studied.
        Add(courseIds[2 % courseIds.Count], now.Date.AddHours(7), now.Date.AddHours(8), completed: true);

        // This week, spread across courses/days (week hours, streak, focus score).
        for (var daysAgo = 1; daysAgo <= 5; daysAgo++)
            Add(courseIds[daysAgo % courseIds.Count], now.Date.AddDays(-daysAgo).AddHours(9), now.Date.AddDays(-daysAgo).AddHours(10), completed: true);

        // Last ~8 weeks (weekly trend chart, anomaly baseline, longest streak).
        for (var week = 1; week <= 8; week++)
            Add(courseIds[week % courseIds.Count], now.Date.AddDays(-7 * week).AddHours(10), now.Date.AddDays(-7 * week).AddHours(12), completed: true);

        // Inside the 400-day history window, older than the 8-week trend range.
        Add(courseIds[1 % courseIds.Count], now.Date.AddDays(-120).AddHours(9), now.Date.AddDays(-120).AddHours(11), completed: true);
        Add(courseIds[2 % courseIds.Count], now.Date.AddDays(-250).AddHours(9), now.Date.AddDays(-250).AddHours(10), completed: true);
        Add(courseIds[0], now.Date.AddDays(-390).AddHours(14), now.Date.AddDays(-390).AddHours(16), completed: true);

        // Beyond the 400-day window, still inside the 3650-day achievements window.
        Add(courseIds[3 % courseIds.Count], now.Date.AddDays(-450).AddHours(9), now.Date.AddDays(-450).AddHours(11), completed: true);
        Add(courseIds[4 % courseIds.Count], now.Date.AddDays(-900).AddHours(11), now.Date.AddDays(-900).AddHours(12), completed: true);
        Add(courseIds[1 % courseIds.Count], now.Date.AddDays(-2000).AddHours(10), now.Date.AddDays(-2000).AddHours(13), completed: true);

        // Beyond even the 3650-day window - excluded from both History and HeavyHistory.
        Add(courseIds[2 % courseIds.Count], now.Date.AddDays(-4000).AddHours(9), now.Date.AddDays(-4000).AddHours(10), completed: true);

        // A few more, mixing completed/not-yet, to comfortably clear ~25 total and add variety.
        for (var i = 1; i <= 6; i++)
            Add(courseIds[i % courseIds.Count], now.Date.AddDays(-i * 3).AddHours(15), now.Date.AddDays(-i * 3).AddHours(16), completed: i % 2 == 0);

        foreach (var session in sessions)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", session)).StatusCode);

        // Goals: one completed with a grade, one still open with a near-term target date.
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

        // Notes: one bound to a course, one general.
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
    /// Assembles DashboardSummaryInput exactly like Index.razor.cs's LoadDataAsync does - fetching
    /// the same nine endpoints through the SAME HttpClient the seed data was written through - and
    /// runs the shared builder locally. This is the "expected" side of the parity assertion.
    /// </summary>
    private static async Task<DashboardSummaryDto> BuildExpectedAsync(HttpClient client, DateTime now)
    {
        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        var allCourses = await client.GetFromJsonAsync<List<CourseDto>>("/api/courses");
        var sessions = await client.GetFromJsonAsync<List<StudySessionDto>>("/api/sessions");
        var history = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={DashboardSummaryBuilder.HistoryDays}&onlyCompleted=false");
        var heavyHistory = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={DashboardSummaryBuilder.AchievementHistoryDays}");
        var goals = await client.GetFromJsonAsync<List<CourseGoalDto>>("/api/coursegoals");
        var studyPrograms = await client.GetFromJsonAsync<List<StudyProgramSummaryDto>>("/api/studyprograms");
        var notes = await client.GetFromJsonAsync<List<NoteDto>>("/api/notes");

        IReadOnlyDictionary<string, int> groupQuotas = CourseCatalog.GroupEctsQuotas;
        if (settings!.ActiveStudyProgramId is int programId)
        {
            var detail = await client.GetFromJsonAsync<StudyProgramDetailDto>($"/api/studyprograms/{programId}");
            groupQuotas = detail!.GroupEctsQuotas;
        }

        var accountInfo = await client.GetFromJsonAsync<AccountInfoDto>("/api/auth/account-info");
        var demoInfo = await client.GetFromJsonAsync<DemoInfoDto>("/api/auth/demo");
        var capabilities = await client.GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");

        var input = new DashboardSummaryInput
        {
            Settings = settings,
            AllCourses = allCourses!,
            Sessions = sessions!,
            History = history!,
            HeavyHistory = heavyHistory,
            Goals = goals!,
            GroupQuotas = groupQuotas,
            StudyPrograms = studyPrograms!,
            Notes = notes!,
            IsOwner = accountInfo?.IsOwner ?? false,
            IsDemo = demoInfo?.Demo ?? false,
            RawBackupSupported = capabilities?.RawBackupSupported ?? true,
            Now = now,
        };
        return DashboardSummaryBuilder.Build(input);
    }

    private static async Task<DashboardSummaryDto> FetchSummaryAsync(HttpClient client, DateTime now)
    {
        var response = await client.GetAsync($"/api/dashboard/summary?now={NowQuery(now)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DashboardSummaryDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    /// <summary>Truncated to whole seconds so the value baked into the query string (and thus the
    /// cache key) is bit-identical to the value used to build the "expected" DTO locally - a raw
    /// DateTime.Now carries sub-second precision the "o"-less query format below would drop.</summary>
    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Unspecified);

    private static string NowQuery(DateTime now) => Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ss"));

    private static void AssertJsonEqual(DashboardSummaryDto expected, DashboardSummaryDto actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
}
