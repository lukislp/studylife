using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StudyLife.Server.Tests;

/// <summary>
/// WebhooksProxyController's auth/configuration gates - the actual proxy relay to a real
/// studylife-webhooks instance is out of scope here, since StudyLifeWebhooks:* is deliberately
/// unset in the test host (same pattern as AiProxyControllerTests - an optional integration
/// stays off by default).
///
/// Unlike AiProxyController, this controller is deliberately NOT session-only: the whole point
/// of studylife-webhooks is that an external program (not the browser client) registers its own
/// subscriptions via the WebhooksApiKey slot - see ApiKeyScopes.Webhooks. So these tests pin the
/// opposite shape from AiProxyControllerTests: a valid API key from the RIGHT slot must succeed
/// (modulo the 503 "not configured" gate), while one from the WRONG slot must be rejected with
/// 403 (authenticated fine, just insufficient scope), not 401.
/// </summary>
public class WebhooksProxyControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WebhooksProxyControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task List_WithoutAnyCredential_ReturnsUnauthorized()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutAnyCredential_ReturnsUnauthorized()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.PostAsJsonAsync("/api/webhooks", new { targetUrl = "https://example.com", events = new[] { "session.completed" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutAnyCredential_ReturnsUnauthorized()
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

    /// <summary>The actual point of this controller: a WebhooksApiKey alone (no session at all)
    /// must be enough to reach it - it only fails past that with 503, because studylife-webhooks
    /// isn't configured in the test host, never with 401/403.</summary>
    [Fact]
    public async Task List_WithValidWebhooksApiKeyAndNoSession_ReturnsServiceUnavailable_NotUnauthorized()
    {
        var sessionClient = _factory.CreateClient();
        var apiKey = await GenerateWebhooksApiKeyAsync(sessionClient);

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/webhooks-api-key/revoke", null);
    }

    [Fact]
    public async Task List_WithAnUnrelatedSlotsApiKey_ReturnsForbidden_NotUnauthorized()
    {
        // A genuinely valid, authenticated key - just not one scoped to Webhooks.* (see
        // ApiKeyScopes.Ha, which has no WebhooksProxy entries at all).
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.GetAsync("/api/webhooks");

        // Authenticated fine (it's a real key) but lacks scope for this endpoint -
        // ApiKeyScopeAuthorizationHandler's "pending requirement" signal maps to 403, not 401.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/ha-api-key/revoke", null); // cleanup, like WhoamiTests
    }

    [Fact]
    public async Task WebhooksApiKey_CannotReachAnUnrelatedEndpoint()
    {
        // The mirror image of the test above: the Webhooks slot's own key must not gain access
        // to endpoints outside ApiKeyScopes.Webhooks either (e.g. /api/timerstate, which every
        // Guard/Tune/Tray-style narrow slot IS scoped to, but Webhooks deliberately is not).
        var sessionClient = _factory.CreateClient();
        var apiKey = await GenerateWebhooksApiKeyAsync(sessionClient);

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/webhooks-api-key/revoke", null);
    }

    private static async Task<string> GenerateWebhooksApiKeyAsync(HttpClient sessionClient)
    {
        var generateResponse = await sessionClient.PostAsync("/api/settings/webhooks-api-key/generate", null);
        var body = (await generateResponse.Content.ReadFromJsonAsync<JsonDocument>())!;
        return body.RootElement.GetProperty("apiKey").GetString()!;
    }
}
