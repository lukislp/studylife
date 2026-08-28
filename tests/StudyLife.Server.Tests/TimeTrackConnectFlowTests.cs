using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// TimeTrack browser-consent connect flow (identity contract v1 §2, generalized to a fourth
/// audience alongside mcp/capture/focusguard - see AuthController.BuildConnectRedirectAsync/
/// RedeemConsentAssertionAsync): POST api/auth/timetrack-connect (session-required, rotates the
/// caller's TIMETRACK key and stakes out a single-use, timetrack-audience assertion) followed by
/// POST api/auth/timetrack-assertion-exchange (unauthenticated/exempt from the gate,
/// server-to-server - the assertion IS the credential). Deliberately mirrors
/// CaptureConnectFlowTests test-for-test - every audience shares the exact same underlying
/// mechanism, so every audience's test suite pins the exact same guarantees, just against the
/// timetrack-prefixed endpoints/DTOs/key slot.
/// </summary>
public class TimeTrackConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TimeTrackConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    // A real chrome.identity.launchWebAuthFlow-style redirect_uri - a perfectly ordinary https
    // origin, no special-casing needed by the server for this audience.
    private const string RedirectUri = "https://abcdefghijklmnopqrstuvwxyzabcdef.chromiumapp.org/";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private static async Task ClearTimeTrackKeyAsync(CustomWebApplicationFactory factory)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.TimeTrackApiKeyHash = null;
            user.TimeTrackApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheTimeTrackKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/timetrack-connect",
            new TimeTrackConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<TimeTrackConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/timetrack-assertion-exchange",
            new TimeTrackAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<TimeTrackAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.TimeTrackApiKey));

        // The exchanged key is really the rotated TimeTrackApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.TimeTrackApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("timetrack", whoamiResult.Credential);

        await ClearTimeTrackKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/timetrack-connect",
            new TimeTrackConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<TimeTrackConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/timetrack-assertion-exchange",
            new TimeTrackAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/timetrack-assertion-exchange",
            new TimeTrackAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await ClearTimeTrackKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/timetrack-assertion-exchange",
            new TimeTrackAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/timetrack-assertion-exchange",
            new TimeTrackAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/timetrack-connect",
            new TimeTrackConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://timetrack.example.com/callback")] // not https, not loopback
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/timetrack-connect",
            new TimeTrackConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
