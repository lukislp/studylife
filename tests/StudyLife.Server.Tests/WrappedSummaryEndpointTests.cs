using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level parity lock for GET /api/wrapped/summary - same shape as
/// DashboardSummaryEndpointTests: seed a realistic dataset through the normal write endpoints,
/// fetch the same raw endpoints Wrapped.razor.cs's OnTextLoadedAsync would through the SAME
/// HttpClient, run WrappedSummaryBuilder.Build locally with that input, and compare the
/// serialized result against the endpoint's own response.
/// </summary>
public class WrappedSummaryEndpointTests
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

        var first = await client.GetAsync($"/api/wrapped/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await client.GetAsync($"/api/wrapped/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/wrapped/summary?now={nowQuery}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var third = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, third.StatusCode);
    }

    [Fact]
    public async Task NoteWrite_ChangesTheResultAndTheEtag()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var now = TruncateToSeconds(DateTime.Now);
        var nowQuery = NowQuery(now);

        var before = await client.GetAsync($"/api/wrapped/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var etagBefore = before.Headers.ETag!.ToString();

        // Notes have no session-history version counter of their own - the notes token in the
        // cache key is the only thing that can invalidate this endpoint after a pure note write,
        // so this test specifically exercises THAT path (SessionWrite is covered by the other
        // summary endpoints' equivalent test). A single note write always changes the notes
        // token (and thus the cache key), but the achievements DTO itself only changes once the
        // notes-taken tier threshold (AchievementCatalog.NotesTiers[0] = 5) is actually crossed -
        // so this writes 5 notes to guarantee the content, not just the key, differs.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/notes", new NoteDto
            {
                Title = $"Wrapped parity note {i}",
                Content = "x",
            })).StatusCode);
        }

        var after = await client.GetAsync($"/api/wrapped/summary?now={nowQuery}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etagBefore, after.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task MissingNow_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/wrapped/summary")).StatusCode);
    }

    [Fact]
    public async Task NowTooFarFromServerClock_Returns400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var farOff = NowQuery(DateTime.Now.AddDays(3));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/wrapped/summary?now={farOff}")).StatusCode);
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
            (await keyClient.GetAsync($"/api/wrapped/summary?now={nowQuery}")).StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// ~25+ sessions spanning the 365-day recap window and well beyond it (up to and past the
    /// 3650-day achievements window), completed and not, plus notes and a course goal - enough to
    /// exercise the achievement thresholds (StudyMetrics.CountUnlockedAchievements) as well as
    /// both history windows' boundaries. courseIds must already be selected in settings.
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

        // Today/this week, including an early-bird and a night-owl session (chronotype/achievements).
        Add(courseIds[0], now.Date.AddHours(6), now.Date.AddHours(7), completed: true);
        Add(courseIds[1 % courseIds.Count], now.Date.AddHours(23), now.Date.AddHours(23).AddMinutes(30), completed: true);
        for (var daysAgo = 1; daysAgo <= 5; daysAgo++)
            Add(courseIds[daysAgo % courseIds.Count], now.Date.AddDays(-daysAgo).AddHours(9), now.Date.AddDays(-daysAgo).AddHours(11), completed: true);

        // Weekend sessions (weekend-warrior style achievement categories).
        var lastSaturday = now.Date;
        while (lastSaturday.DayOfWeek != DayOfWeek.Saturday) lastSaturday = lastSaturday.AddDays(-1);
        Add(courseIds[2 % courseIds.Count], lastSaturday.AddHours(10), lastSaturday.AddHours(13), completed: true);

        // Inside the 365-day recap window, spread over months.
        for (var week = 2; week <= 40; week += 4)
            Add(courseIds[week % courseIds.Count], now.Date.AddDays(-7 * week).AddHours(10), now.Date.AddDays(-7 * week).AddHours(12), completed: true);

        // Beyond the recap window, inside the 3650-day achievements window.
        Add(courseIds[3 % courseIds.Count], now.Date.AddDays(-500).AddHours(9), now.Date.AddDays(-500).AddHours(11), completed: true);
        Add(courseIds[4 % courseIds.Count], now.Date.AddDays(-2000).AddHours(9), now.Date.AddDays(-2000).AddHours(12), completed: true);

        // Beyond even the 3650-day window - excluded from both windows.
        Add(courseIds[0], now.Date.AddDays(-4000).AddHours(9), now.Date.AddDays(-4000).AddHours(10), completed: true);

        foreach (var session in sessions)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", session)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/coursegoals/{courseIds[0]}", new CourseGoalDto
        {
            CourseId = courseIds[0],
            CourseName = "x",
            Grade = 2.0m,
            CompletedTopics = "",
            CompletedAt = now.AddDays(-90),
        })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Wrapped seed note",
            Content = "For the achievements notes-taken category.",
        })).StatusCode);
    }

    /// <summary>
    /// Assembles WrappedSummaryInput exactly like Wrapped.razor.cs's OnTextLoadedAsync does -
    /// fetching the same endpoints through the SAME HttpClient the seed data was written through -
    /// and runs the shared builder locally. This is the "expected" side of the parity assertion.
    /// </summary>
    private static async Task<WrappedSummaryDto> BuildExpectedAsync(HttpClient client, DateTime now)
    {
        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        var allCourses = await client.GetFromJsonAsync<List<CourseDto>>("/api/courses");
        var periodHistory = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={WrappedSummaryBuilder.PeriodHistoryDays}");
        var allTimeHistory = await client.GetFromJsonAsync<List<StudySessionDto>>(
            $"/api/sessions/history?days={WrappedSummaryBuilder.AllTimeHistoryDays}");
        var notes = await client.GetFromJsonAsync<List<NoteDto>>("/api/notes");
        var studyPrograms = await client.GetFromJsonAsync<List<StudyProgramSummaryDto>>("/api/studyprograms");

        IReadOnlyDictionary<string, int> groupQuotas = CourseCatalog.GroupEctsQuotas;
        if (settings!.ActiveStudyProgramId is int programId)
        {
            var detail = await client.GetFromJsonAsync<StudyProgramDetailDto>($"/api/studyprograms/{programId}");
            groupQuotas = detail!.GroupEctsQuotas;
        }

        var input = new WrappedSummaryInput
        {
            Settings = settings,
            AllCourses = allCourses!,
            PeriodHistory = periodHistory!,
            AllTimeHistory = allTimeHistory!,
            GroupQuotas = groupQuotas,
            StudyPrograms = studyPrograms!,
            Notes = notes!,
            Now = now,
        };
        return WrappedSummaryBuilder.Build(input);
    }

    private static async Task<WrappedSummaryDto> FetchSummaryAsync(HttpClient client, DateTime now)
    {
        var response = await client.GetAsync($"/api/wrapped/summary?now={NowQuery(now)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<WrappedSummaryDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Unspecified);

    private static string NowQuery(DateTime now) => Uri.EscapeDataString(now.ToString("yyyy-MM-ddTHH:mm:ss"));

    private static void AssertJsonEqual(WrappedSummaryDto expected, WrappedSummaryDto actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
}
