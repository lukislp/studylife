using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// FocusGuard browser-consent connect flow (identity contract v1 §2, generalized to a third
/// audience alongside mcp/capture - see AuthController.BuildConnectRedirectAsync/
/// RedeemConsentAssertionAsync): POST api/auth/focusguard-connect (session-required, rotates the
/// caller's FOCUSGUARD key and stakes out a single-use, focusguard-audience assertion) followed by
/// POST api/auth/focusguard-assertion-exchange (unauthenticated/exempt from the gate,
/// server-to-server - the assertion IS the credential). Deliberately mirrors
/// CaptureConnectFlowTests test-for-test - all three audiences share the exact same underlying
/// mechanism, so all three test suites pin the exact same guarantees, just against the
/// focusguard-prefixed endpoints/DTOs/key slot.
/// </summary>
public class FocusGuardConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public FocusGuardConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    // A real chrome.identity.launchWebAuthFlow-style redirect_uri - a perfectly ordinary https
    // origin, no special-casing needed by the server for this audience.
    private const string RedirectUri = "https://abcdefghijklmnopqrstuvwxyzabcdef.chromiumapp.org/";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private static async Task ClearFocusGuardKeyAsync(CustomWebApplicationFactory factory)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.FocusGuardApiKeyHash = null;
            user.FocusGuardApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheFocusGuardKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/focusguard-connect",
            new FocusGuardConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<FocusGuardConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/focusguard-assertion-exchange",
            new FocusGuardAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<FocusGuardAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.FocusGuardApiKey));

        // The exchanged key is really the rotated FocusGuardApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.FocusGuardApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("focusguard", whoamiResult.Credential);

        await ClearFocusGuardKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/focusguard-connect",
            new FocusGuardConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<FocusGuardConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/focusguard-assertion-exchange",
            new FocusGuardAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/focusguard-assertion-exchange",
            new FocusGuardAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await ClearFocusGuardKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focusguard-assertion-exchange",
            new FocusGuardAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focusguard-assertion-exchange",
            new FocusGuardAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focusguard-connect",
            new FocusGuardConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://focusguard.example.com/callback")] // not https, not loopback
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/focusguard-connect",
            new FocusGuardConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
