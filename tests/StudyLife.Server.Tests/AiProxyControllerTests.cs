using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StudyLife.Server.Tests;

/// <summary>
/// AiProxyController's auth/configuration gates - the actual proxy relay to a real
/// studylife-ai instance is covered by the live end-to-end check instead (see
/// docs/decisions.md "M4.5 Multi-user support"), since StudyLifeAi:* is deliberately unset
/// in the test host (same pattern as Apns:* - an optional integration stays off by default).
/// </summary>
public class AiProxyControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiProxyControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/ai/chat")]
    [InlineData("/api/ai/agent")]
    [InlineData("/api/ai/agent/confirm")]
    public async Task Proxy_WithoutASession_ReturnsUnauthorized(string path)
    {
        // A bare API key (no X-Session-Token) must not be enough - only a real logged-in
        // session may drive the AI agent, same reasoning as SettingsController's ai-api-key
        // group (SessionUser pattern).
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/ai/chat")]
    [InlineData("/api/ai/agent")]
    [InlineData("/api/ai/agent/confirm")]
    public async Task Proxy_WithASessionButNoStudyLifeAiConfigured_ReturnsServiceUnavailable(string path)
    {
        var client = _factory.CreateClient(); // session-authenticated by default, see CustomWebApplicationFactory

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// New pinning test (audit finding A3 refactor): the manual "SessionUser" check this
    /// controller used to do is now [Authorize(Policy = "SessionOnly")] on the whole class - a
    /// VALID API key (genuinely authenticated, just not via a passkey session) must still be
    /// rejected with 401, not the framework's normal "authenticated but not permitted" 403 (see
    /// AlwaysChallengeAuthorizationMiddlewareResultHandler). Otherwise identical to the former
    /// manual check's observable behavior, but previously untested with a REAL key (only the
    /// fully-anonymous case above was covered).
    /// </summary>
    [Fact]
    public async Task Proxy_WithValidApiKeyButNoSession_ReturnsUnauthorized_NotForbidden()
    {
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.PostAsJsonAsync("/api/ai/chat", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/ha-api-key/revoke", null); // cleanup, like WhoamiTests
    }
}
