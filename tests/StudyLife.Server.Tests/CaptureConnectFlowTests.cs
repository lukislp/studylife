using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Capture browser-consent connect flow (identity contract v1 §2, generalized to a second
/// audience alongside mcp - see AuthController.BuildConnectRedirectAsync/RedeemConsentAssertionAsync):
/// POST api/auth/capture-connect (session-required, rotates the caller's CAPTURE key and stakes
/// out a single-use, capture-audience assertion) followed by POST api/auth/capture-assertion-exchange
/// (unauthenticated/exempt from the gate, server-to-server - the assertion IS the credential).
/// Deliberately mirrors McpConnectFlowTests test-for-test (same redirect URI shape, same
/// assertions) - both audiences share the exact same underlying mechanism, so both test suites
/// pin the exact same guarantees, just against the capture-prefixed endpoints/DTOs/key slot.
/// </summary>
public class CaptureConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CaptureConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    // A real chrome.identity.launchWebAuthFlow-style redirect_uri - a perfectly ordinary https
    // origin, no special-casing needed by the server for this audience.
    private const string RedirectUri = "https://abcdefghijklmnopqrstuvwxyzabcdef.chromiumapp.org/";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private static async Task ClearCaptureKeyAsync(CustomWebApplicationFactory factory)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.CaptureApiKeyHash = null;
            user.CaptureApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheCaptureKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<CaptureConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<CaptureAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.CaptureApiKey));

        // The exchanged key is really the rotated CaptureApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.CaptureApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("capture", whoamiResult.Credential);

        await ClearCaptureKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<CaptureConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await ClearCaptureKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/capture-assertion-exchange",
            new CaptureAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://capture.example.com/callback")] // not https, not loopback
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/capture-connect",
            new CaptureConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
