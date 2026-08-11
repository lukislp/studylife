using System.Net;
using System.Net.Http.Json;

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
}
