using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// GET /api/auth/whoami (identity contract v1 §1): lets a satellite resolve the REAL AuthUserId
/// behind whatever credential it holds instead of inventing its own identity (audit A1). Unlike
/// the rest of /api/auth, whoami deliberately goes through the normal gate resolution in
/// Program.cs (see the carve-out there) so it can report which credential actually matched.
/// </summary>
public class WhoamiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WhoamiTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Whoami_WithSessionToken_ReturnsUserIdAndSessionCredential()
    {
        var client = _factory.CreateClient(); // carries the seeded test user's session token

        var response = await client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.UserId);
        Assert.Equal("session", result.Credential);
    }

    [Theory]
    [InlineData("ha-api-key", "ha")]
    [InlineData("ai-api-key", "ai")]
    [InlineData("mcp-api-key", "mcp")]
    [InlineData("capture-api-key", "capture")]
    public async Task Whoami_WithApiKey_ReturnsUserIdAndMatchingSlot(string slotEndpoint, string expectedCredential)
    {
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync($"/api/settings/{slotEndpoint}/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();
        Assert.False(string.IsNullOrEmpty(apiKey));

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var response = await keyClient.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.UserId);
        Assert.Equal(expectedCredential, result.Credential);

        // Cleanup: revoke so the four slot tests in this Theory don't interfere with each other
        // or with sibling test classes sharing the same AuthUser 1 on the shared factory.
        await sessionClient.PostAsync($"/api/settings/{slotEndpoint}/revoke", null);
    }

    [Fact]
    public async Task Whoami_WithoutAnyCredential_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Whoami_WithInvalidApiKey_ReturnsUnauthorized()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, "does-not-exist");

        var response = await client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Audit finding A12a: the ?apiKey= query-string fallback was removed from
    /// StudyLifeAuthenticationHandler - a genuinely valid key must now ONLY authenticate via the
    /// X-Api-Key header, never via the URL. Uses a real, freshly generated key (not a placeholder)
    /// so this actually pins "the query string is never even consulted", not just "a bad key
    /// fails" (which Whoami_WithInvalidApiKey_ReturnsUnauthorized already covers).
    /// </summary>
    [Fact]
    public async Task Whoami_WithValidApiKeyAsQueryParam_ReturnsUnauthorized()
    {
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();
        Assert.False(string.IsNullOrEmpty(apiKey));

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.GetAsync($"/api/auth/whoami?apiKey={apiKey}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await sessionClient.PostAsync("/api/settings/ha-api-key/revoke", null);
    }
}
