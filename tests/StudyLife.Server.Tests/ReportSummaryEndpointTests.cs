using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level parity lock for GET /api/report/summary - same shape as
/// DashboardSummaryEndpointTests: seed a realistic dataset through the normal write endpoints,
/// fetch the same raw endpoints Report.razor.cs's OnTextLoadedAsync would through the SAME
/// HttpClient, run ReportSummaryBuilder.Build locally with that input, and compare the serialized
/// result against the endpoint's own response.
/// </summary>
public class ReportSummaryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Summary_MatchesInputAssembledFromTheSameEndpoints()
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

    [Fact]
    public async Task Summary_SecondCallWithinTtl_ServedFromCache_WithEtagAnd304()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var nowQuery = NowQuery(now);

        var first = await client.GetAsync($"/api/report/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.GetAsync($"/api/report/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/report/summary?now={nowQuery}");
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

        var before = await client.GetAsync($"/api/report/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var etagBefore = before.Headers.ETag!.ToString();

        var created = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "x",
            CourseColor = "#000000",
            StartTime = now.AddHours(-2),
            EndTime = now.AddHours(-1),
            IsCompleted = true,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var after = await client.GetAsync($"/api/report/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etagBefore, after.Headers.ETag!.ToString());

        var dto = await after.Content.ReadFromJsonAsync<ReportSummaryDto>();
        Assert.Contains(dto!.CourseRows, r => r.Course.Id == 1 && r.SessionCount > 0);
    }

    [Fact]
    public async Task MissingNow_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/report/summary")).StatusCode);
    }

    [Fact]
    public async Task NowTooFarFromServerClock_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var farOff = NowQuery(DateTime.Now.AddDays(3));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/report/summary?now={farOff}")).StatusCode);
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
            (await keyClient.GetAsync($"/api/report/summary?now={nowQuery}")).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// ~25+ sessions spanning past/today, completed and not, deliberately reaching past
    /// ReportSummaryBuilder.HistoryDays' 10-year window in the OTHER direction (older-than-3650)
    /// so the exclusion boundary is also exercised, not just "everything recent". Plus course
    /// goals (grade + open target date) mirroring DashboardSummaryEndpointTests' seed shape.
    /// courseIds must already be selected in settings by the caller.
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

        // Today, already studied, and an in-progress one that only counts once its end passes.
        Add(courseIds[0], now.Date.AddHours(7), now.Date.AddHours(8), completed: true);
        Add(courseIds[1 % courseIds.Count], now.AddHours(-1), now.AddHours(1), completed: false);

        // This week, spread across courses/days.
        for (var daysAgo = 1; daysAgo <= 5; daysAgo++)
            Add(courseIds[daysAgo % courseIds.Count], now.Date.AddDays(-daysAgo).AddHours(9), now.Date.AddDays(-daysAgo).AddHours(10), completed: true);

        // Several months back, mixing courses/completion.
        for (var week = 1; week <= 8; week++)
            Add(courseIds[week % courseIds.Count], now.Date.AddDays(-7 * week).AddHours(10), now.Date.AddDays(-7 * week).AddHours(12), completed: week % 2 == 0);

        // Older than a year, still inside the 10-year window.
        Add(courseIds[2 % courseIds.Count], now.Date.AddDays(-400).AddHours(9), now.Date.AddDays(-400).AddHours(11), completed: true);
        Add(courseIds[3 % courseIds.Count], now.Date.AddDays(-1200).AddHours(9), now.Date.AddDays(-1200).AddHours(11), completed: true);

        // Beyond the 3650-day window - excluded from History entirely.
        Add(courseIds[4 % courseIds.Count], now.Date.AddDays(-4000).AddHours(9), now.Date.AddDays(-4000).AddHours(10), completed: true);

        foreach (var session in sessions)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", session)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[0]}", new CourseGoalDto
        {
            CourseId = courseIds[0],
            CourseName = "x",
            Grade = 1.7m,
            CompletedTopics = "",
            CompletedAt = now.AddDays(-60),
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[1 % courseIds.Count]}", new CourseGoalDto
        {
            CourseId = courseIds[1 % courseIds.Count],
            CourseName = "x",
            TargetDate = now.AddDays(15),
            CompletedTopics = "",
        })).StatusCode);
    }

    /// <summary>
    /// Assembles ReportSummaryInput exactly like Report.razor.cs's OnTextLoadedAsync does -
    /// fetching the same endpoints through the SAME HttpClient the seed data was written through -
    /// and runs the shared builder locally. This is the "expected" side of the parity assertion.
    /// </summary>
    private static async Task<ReportSummaryDto> BuildExpectedAsync(HttpClient client, DateTime now)
    {
        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        var allCourses = await client.GetFromJsonAsync<List<CourseDto>>("/api/courses");
        var goals = await client.GetFromJsonAsync<List<CourseGoalDto>>("/api/coursegoals");
        var history = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={ReportSummaryBuilder.HistoryDays}");
        var studyPrograms = await client.GetFromJsonAsync<List<StudyProgramSummaryDto>>("/api/studyprograms");

        IReadOnlyDictionary<string, int> groupQuotas = CourseCatalog.GroupEctsQuotas;
        if (settings!.ActiveStudyProgramId is int programId)
        {
            var detail = await client.GetFromJsonAsync<StudyProgramDetailDto>($"/api/studyprograms/{programId}");
            groupQuotas = detail!.GroupEctsQuotas;
        }

        var input = new ReportSummaryInput
        {
            Settings = settings,
            AllCourses = allCourses!,
            Goals = goals!,
            History = history!,
            GroupQuotas = groupQuotas,
            StudyPrograms = studyPrograms!,
            Now = now,
        };
        return ReportSummaryBuilder.Build(input);
    }

    private static async Task<ReportSummaryDto> FetchSummaryAsync(HttpClient client, DateTime now)
    {
        var response = await client.GetAsync($"/api/report/summary?now={NowQuery(now)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReportSummaryDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Unspecified);

    private static string NowQuery(DateTime now) => Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ss"));

    private static void AssertJsonEqual(ReportSummaryDto expected, ReportSummaryDto actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
}
