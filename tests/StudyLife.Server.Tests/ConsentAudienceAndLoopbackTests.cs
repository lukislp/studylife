using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Two cross-cutting guarantees of the generalized consent connect flow (identity contract v1
/// §2, AuthController.BuildConnectRedirectAsync/RedeemConsentAssertionAsync) that don't belong to
/// either McpConnectFlowTests or CaptureConnectFlowTests specifically, since they exercise BOTH
/// audiences together:
///
/// 1. Audience isolation: an assertion minted for one audience (mcp/capture) must be rejected at
///    the OTHER audience's exchange endpoint, and - the consciously chosen semantics documented on
///    RedeemConsentAssertionAsync - that misdirected attempt must NOT consume it, so the legitimate
///    holder can still redeem it at the correct endpoint afterward.
/// 2. The RFC 8252 §8.3 native-app loopback exception (IsAllowedRedirectUri): exactly
///    http://127.0.0.1:&lt;port&gt;/... and http://localhost:&lt;port&gt;/... are accepted
///    alongside https, any other http host is rejected, and https itself is unaffected.
/// </summary>
public class ConsentAudienceAndLoopbackTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ConsentAudienceAndLoopbackTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string RedirectUri = "https://mcp.example.com/auth/studylife/callback";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private async Task ClearBothKeysAsync()
    {
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.McpApiKeyHash = null;
            user.McpApiKeyCreatedAt = null;
            user.CaptureApiKeyHash = null;
            user.CaptureApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task McpAssertion_PresentedAtCaptureExchange_IsRejectedButNotConsumed_AndStillRedeemableAtMcpExchange()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<McpConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        // Wrong audience: rejected...
        var wrongAudience = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        // ...but NOT consumed: the legitimate mcp exchange still succeeds afterward.
        var correctAudience = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, correctAudience.StatusCode);
        var result = await correctAudience.Content.ReadFromJsonAsync<McpAssertionExchangeResponseDto>();
        Assert.Equal(1, result!.UserId);

        await ClearBothKeysAsync();
    }

    [Fact]
    public async Task CaptureAssertion_PresentedAtMcpExchange_IsRejectedButNotConsumed_AndStillRedeemableAtCaptureExchange()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = "https://abc.chromiumapp.org/", State = "s" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<CaptureConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var wrongAudience = await anon.PostAsJsonAsync("/api/auth/mcp-assertion-exchange",
            new McpAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        var correctAudience = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, correctAudience.StatusCode);
        var result = await correctAudience.Content.ReadFromJsonAsync<CaptureAssertionExchangeResponseDto>();
        Assert.Equal(1, result!.UserId);

        await ClearBothKeysAsync();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/callback")]
    [InlineData("http://127.0.0.1:1/")] // any port
    [InlineData("http://localhost:54321/oauth/callback")]
    [InlineData("http://LOCALHOST:8765/callback")] // host match is case-insensitive
    public async Task Connect_WithRfc8252LoopbackRedirectUri_IsAccepted(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<McpConnectResponseDto>();
        Assert.StartsWith(redirectUri, result!.RedirectTo);

        await ClearBothKeysAsync();
    }

    [Theory]
    [InlineData("http://evil.com/callback")] // arbitrary http host - must never be accepted
    [InlineData("http://127.0.0.1.evil.com/callback")] // host-confusion attempt
    [InlineData("http://127.0.0.1:8765@evil.com/callback")] // userinfo-confusion attempt
    [InlineData("http://192.168.1.1:8765/callback")] // a real LAN IP, but not the loopback literal
    [InlineData("ftp://127.0.0.1:8765/callback")] // non-http(s) scheme entirely
    public async Task Connect_WithNonLoopbackHttpRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithConfiguredHttpsRedirectUri_IsAccepted()
    {
        // RedirectUri is on the test factory's Consent:AllowedRedirectUris:mcp list.
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ClearBothKeysAsync();
    }

    /// <summary>2026-09 audit S1: an https callback that is NOT on the audience's allow-list
    /// used to be accepted (any absolute https URL passed), handing the single-use assertion -
    /// and with it the freshly rotated key, via the anonymous exchange endpoint - to whoever
    /// controls that host. Must be a 400 now, and the key slot must stay untouched (no rotation
    /// side effect for a rejected request).</summary>
    [Theory]
    [InlineData("https://attacker.example/cb")]
    [InlineData("https://mcp.example.com/auth/studylife/callback/")] // near-miss of the configured URI
    [InlineData("https://mcp.example.com.attacker.example/auth/studylife/callback")]
    public async Task McpConnect_WithUnlistedHttpsRedirectUri_ReturnsBadRequest_AndDoesNotRotateKey(string redirectUri)
    {
        await ClearBothKeysAsync();
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/mcp-connect",
            new McpConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var hash = await _factory.WithDbAsync(db => db.AuthUsers.Where(u => u.Id == 1).Select(u => u.McpApiKeyHash).FirstAsync());
        Assert.Null(hash);
    }

    [Theory]
    [InlineData("https://attacker.example/cb")]
    [InlineData("http://127.0.0.1:8765/callback")] // loopback is a native-app shape, never a browser extension's
    [InlineData("https://chromiumapp.org/")] // bare suffix host without an extension id
    public async Task CaptureConnect_WithNonExtensionRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
