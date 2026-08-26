using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// MCP OAuth connect flow (identity contract v1 §2): POST api/auth/mcp-connect (session-required,
/// rotates the caller's MCP key and stakes out a single-use assertion) followed by
/// POST api/auth/mcp-assertion-exchange (unauthenticated/exempt from the gate, server-to-server -
/// the assertion IS the credential). Both live under /api/auth, so ApiKeyTestHelpers.
/// CreateClientWithKey(factory, null) gives a genuinely anonymous client for the exchange step,
/// exactly like AuthHandoffTests does for the sibling PKCE handoff flow.
/// </summary>
public class McpConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public McpConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string RedirectUri = "https://mcp.example.com/auth/studylife/callback";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheMcpKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<McpConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<McpAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.McpApiKey));

        // The exchanged key is really the rotated McpApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.McpApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("mcp", whoamiResult.Credential);

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.McpApiKeyHash = null;
            user.McpApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<McpConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.McpApiKeyHash = null;
            user.McpApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://mcp.example.com/callback")] // not https
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
