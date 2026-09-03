using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The metrics endpoints are cached through CacheHelper since the 2026-09 audit (P2). What must
/// hold: a repeated call answers 304 to the ETag, a course-goal write (which bumps the settings
/// version) is visible on the very next call instead of after the TTL, and an unresolvable
/// programme is still a 404, never a cached body.
/// </summary>
public class MetricsCachingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public MetricsCachingTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private const string Url = "/api/metrics/summary?now=2026-03-10T12:00:00";

    [Fact]
    public async Task Summary_IsRevalidatable_AndReflectsAGoalWriteImmediately()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.ActiveStudyProgramId = null);
        var first = await _client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        Assert.True(first.Headers.CacheControl?.NoCache);
        Assert.False(first.Headers.CacheControl?.NoStore);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, Url);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await _client.SendAsync(conditional)).StatusCode);

        // A grade on a course goal changes the average grade - must show up at once.
        var courseId = CourseCatalog.AppliedAICourses[0].Id;
        var put = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", new CourseGoalDto
        {
            CourseId = courseId,
            CourseName = "x",
            Grade = 1.3m,
            CompletedTopics = "",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await _client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(first.Headers.ETag!.ToString(), after.Headers.ETag!.ToString());
        var dto = await after.Content.ReadFromJsonAsync<MetricsSummaryDto>();
        Assert.Equal(1.3m, dto!.AverageGrade!.Value, precision: 1);
    }

    [Fact]
    public async Task UnknownProgramme_IsNotFound_NotCached()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/metrics/summary?program=999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/metrics/achievements?program=999999")).StatusCode);
    }

    [Fact]
    public async Task Achievements_IsRevalidatable()
    {
        var first = await _client.GetAsync("/api/metrics/achievements");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/metrics/achievements");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await _client.SendAsync(conditional)).StatusCode);
    }
}
