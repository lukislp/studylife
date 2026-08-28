using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// FocusTunes browser-consent connect flow (identity contract v1 §2, generalized to a fifth
/// audience alongside mcp/capture/focusguard/timetrack - see
/// AuthController.BuildConnectRedirectAsync/RedeemConsentAssertionAsync): POST
/// api/auth/focustunes-connect (session-required, rotates the caller's FOCUSTUNES key and stakes
/// out a single-use, focustunes-audience assertion) followed by POST
/// api/auth/focustunes-assertion-exchange (unauthenticated/exempt from the gate, server-to-server
/// - the assertion IS the credential). Deliberately mirrors CaptureConnectFlowTests test-for-test
/// - every audience shares the exact same underlying mechanism, so every audience's test suite
/// pins the exact same guarantees, just against the focustunes-prefixed endpoints/DTOs/key slot.
/// </summary>
public class FocusTunesConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public FocusTunesConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    // A real chrome.identity.launchWebAuthFlow-style redirect_uri - a perfectly ordinary https
    // origin, no special-casing needed by the server for this audience.
    private const string RedirectUri = "https://abcdefghijklmnopqrstuvwxyzabcdef.chromiumapp.org/";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private static async Task ClearFocusTunesKeyAsync(CustomWebApplicationFactory factory)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.FocusTunesApiKeyHash = null;
            user.FocusTunesApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheFocusTunesKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/focustunes-connect",
            new FocusTunesConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<FocusTunesConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/focustunes-assertion-exchange",
            new FocusTunesAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<FocusTunesAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.FocusTunesApiKey));

        // The exchanged key is really the rotated FocusTunesApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.FocusTunesApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("focustunes", whoamiResult.Credential);

        await ClearFocusTunesKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/focustunes-connect",
            new FocusTunesConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<FocusTunesConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/focustunes-assertion-exchange",
            new FocusTunesAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/focustunes-assertion-exchange",
            new FocusTunesAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await ClearFocusTunesKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focustunes-assertion-exchange",
            new FocusTunesAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focustunes-assertion-exchange",
            new FocusTunesAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/focustunes-connect",
            new FocusTunesConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://focustunes.example.com/callback")] // not https, not loopback
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/focustunes-connect",
            new FocusTunesConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
