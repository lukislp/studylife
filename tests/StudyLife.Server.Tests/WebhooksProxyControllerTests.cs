using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StudyLife.Server.Tests;

/// <summary>
/// WebhooksProxyController's auth/configuration gates - the actual proxy relay to a real
/// studylife-webhooks instance is out of scope here, since StudyLifeWebhooks:* is deliberately
/// unset in the test host (same pattern as AiProxyControllerTests - an optional integration
/// stays off by default).
/// </summary>
public class WebhooksProxyControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WebhooksProxyControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task List_WithoutASession_ReturnsUnauthorized()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutASession_ReturnsUnauthorized()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.PostAsJsonAsync("/api/webhooks", new { targetUrl = "https://example.com", events = new[] { "session.completed" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutASession_ReturnsUnauthorized()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.DeleteAsync("/api/webhooks/some-id");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithASessionButNoStudyLifeWebhooksConfigured_ReturnsServiceUnavailable()
    {
        var client = _factory.CreateClient(); // session-authenticated by default, see CustomWebApplicationFactory

        var response = await client.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Same pinning reasoning as AiProxyControllerTests.
    /// Proxy_WithValidApiKeyButNoSession_ReturnsUnauthorized_NotForbidden: [Authorize(Policy =
    /// SessionOnly)] must reject a genuinely valid API key with 401, not 403.</summary>
    [Fact]
    public async Task List_WithValidApiKeyButNoSession_ReturnsUnauthorized_NotForbidden()
    {
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/ha-api-key/revoke", null); // cleanup, like WhoamiTests
    }
}
