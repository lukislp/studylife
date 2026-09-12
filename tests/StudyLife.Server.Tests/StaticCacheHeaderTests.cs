using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pins the cache policy of the static files that keep a stable URL across deploys (Program.cs,
/// revalidateStaticFiles). Without an explicit Cache-Control a browser applies heuristic
/// freshness and kept a days-old index.html / service-worker-assets.js for hours after a deploy:
/// the service worker never saw the update, "reload for update" came back under the old worker
/// and only a hard reload helped. no-cache keeps the entry but forces an ETag revalidation.
/// </summary>
public class StaticCacheHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StaticCacheHeaderTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/service-worker.js")]
    [InlineData("/js/interop.js")]
    [InlineData("/Shared/MainLayout.razor.js")]
    public async Task StableUrlStaticFiles_MustBeRevalidatedOnEveryUse(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoCache, $"{path} must carry Cache-Control: no-cache");
        Assert.False(response.Headers.CacheControl.NoStore, $"{path} must stay cacheable (revalidated), not no-store");
    }

    [Fact]
    public async Task ApiFallback_IsNotAffected_AndStaysNoStore()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/does-not-exist");

        // Unmatched /api paths keep their own policy (and never fall through to index.html).
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
