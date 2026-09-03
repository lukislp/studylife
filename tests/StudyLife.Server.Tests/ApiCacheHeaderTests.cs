using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The blanket "Cache-Control: no-store" middleware for /api (Program.cs) must NOT override a
/// Cache-Control the controller set itself - it used to, which made the browser drop every
/// CacheHelper response on the floor and never send If-None-Match again (2026-09 audit L1).
/// Pins both halves: CacheHelper endpoints keep "private, no-cache" + ETag and answer 304 to a
/// matching If-None-Match, while endpoints without their own directive still get no-store.
/// </summary>
public class ApiCacheHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiCacheHeaderTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/sessions")]
    [InlineData("/api/sessions/history?days=30")]
    [InlineData("/api/settings")]
    public async Task CacheHelperEndpoints_KeepRevalidatableCacheControl_AndAnswer304(string url)
    {
        var first = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertRevalidatable(first);
        Assert.NotNull(first.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var second = await _client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        AssertRevalidatable(second);
    }

    /// <summary>"private, no-cache" - HttpClient re-serializes the directives in its own order,
    /// so compare the parsed flags rather than the string.</summary>
    private static void AssertRevalidatable(HttpResponseMessage response)
    {
        var cc = response.Headers.CacheControl;
        Assert.NotNull(cc);
        Assert.True(cc!.Private);
        Assert.True(cc.NoCache);
        Assert.False(cc.NoStore, "no-store would forbid the browser from keeping the ETag at all");
    }

    [Fact]
    public async Task EndpointWithoutOwnDirective_StillGetsNoStore()
    {
        var response = await _client.GetAsync("/api/auth/account-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "endpoints without their own directive must still get no-store");
    }

    [Fact]
    public async Task SessionWrite_ChangesTheEtag_SoAStaleIfNoneMatchGetsAFreshBody()
    {
        var before = await _client.GetAsync("/api/sessions");
        var etag = before.Headers.ETag!.ToString();

        var now = DateTime.Now;
        var created = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 31,
            CourseName = "x",
            CourseColor = "#000000",
            StartTime = now.AddDays(3),
            EndTime = now.AddDays(3).AddHours(1),
            IsCompleted = false,
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var after = await _client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etag, after.Headers.ETag!.ToString());
    }
}
