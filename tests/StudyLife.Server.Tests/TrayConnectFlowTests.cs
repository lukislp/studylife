using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Tray browser-consent connect flow (identity contract v1 §2, generalized to a fifth audience
/// alongside mcp/capture/focusguard/focustunes - see
/// AuthController.BuildConnectRedirectAsync/RedeemConsentAssertionAsync): POST
/// api/auth/tray-connect (session-required, rotates the caller's TRAY key and stakes out a
/// single-use, tray-audience assertion) followed by POST api/auth/tray-assertion-exchange
/// (unauthenticated/exempt from the gate, server-to-server - the assertion IS the credential).
/// Deliberately mirrors FocusTunesConnectFlowTests test-for-test - every audience shares the
/// exact same underlying mechanism, so every audience's test suite pins the exact same
/// guarantees, just against the tray-prefixed endpoints/DTOs/key slot. The one real difference:
/// studylife-tray is a native app, not a browser extension, so its real redirect_uri is an RFC
/// 8252 http://127.0.0.1:&lt;port&gt;/callback loopback rather than a chromiumapp.org origin -
/// covered below alongside the same https-origin case the other audiences use, to confirm both
/// remain valid for this one.
/// </summary>
public class TrayConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TrayConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    // studylife-tray is a native desktop app: its ConnectFlow always uses an RFC 8252 loopback
    // callback, and since the per-audience allow-list (ConsentRedirectPolicy) that is the ONLY
    // built-in shape the tray audience accepts - the chromiumapp.org URI the browser-extension
    // flow tests use would be rejected here with 400.
    private const string RedirectUri = "http://127.0.0.1:41999/callback";
    private const string LoopbackRedirectUri = "http://127.0.0.1:51823/callback";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private static async Task ClearTrayKeyAsync(CustomWebApplicationFactory factory)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.TrayApiKeyHash = null;
            user.TrayApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndRotatesTheTrayKey()
    {
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/tray-connect",
            new TrayConnectRequestDto { RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<TrayConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.False(string.IsNullOrEmpty(assertion));
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<TrayAssertionExchangeResponseDto>();
        Assert.NotNull(exchangeResult);
        Assert.Equal(1, exchangeResult!.UserId); // the REAL AuthUserId, not a hash of the key
        Assert.False(string.IsNullOrEmpty(exchangeResult.TrayApiKey));

        // The exchanged key is really the rotated TrayApiKeyHash slot, usable at the gate.
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.TrayApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoamiResult!.UserId);
        Assert.Equal("tray", whoamiResult.Credential);

        await ClearTrayKeyAsync(_factory);
    }

    [Fact]
    public async Task ConnectThenExchange_WithRfc8252LoopbackRedirectUri_Succeeds()
    {
        // studylife-tray's actual real-world redirect_uri - a loopback HTTP listener the app
        // itself runs, not a browser extension's chromiumapp.org origin.
        var sessionClient = _factory.CreateClient();

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/tray-connect",
            new TrayConnectRequestDto { RedirectUri = LoopbackRedirectUri, State = "s" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<TrayConnectResponseDto>();
        Assert.NotNull(connectResult);
        Assert.StartsWith(LoopbackRedirectUri, connectResult!.RedirectTo);

        var (assertion, _) = ParseRedirectTo(connectResult.RedirectTo);
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);

        await ClearTrayKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/tray-connect",
            new TrayConnectRequestDto { RedirectUri = RedirectUri, State = "s" });
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<TrayConnectResponseDto>();
        var (assertion, _) = ParseRedirectTo(connectResult!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await ClearTrayKeyAsync(_factory);
    }

    [Fact]
    public async Task AssertionExchange_WithGarbageAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = "not-a-real-assertion" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithEmptyAssertion_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/tray-assertion-exchange",
            new TrayAssertionExchangeRequestDto { Assertion = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.PostAsJsonAsync("/api/auth/tray-connect",
            new TrayConnectRequestDto { RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://tray.example.com/callback")] // not https, not loopback
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Connect_WithInvalidRedirectUri_ReturnsBadRequest(string redirectUri)
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/tray-connect",
            new TrayConnectRequestDto { RedirectUri = redirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
