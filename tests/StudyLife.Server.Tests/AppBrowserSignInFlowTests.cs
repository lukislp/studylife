// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.JSInterop;
using NSubstitute;
using StudyLife.Client.Services;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The native app's "sign in in the browser" flow end to end, with the real client code the auth
/// page runs (AppReturnContext behind the browser's SessionHandler, whose token store is empty in
/// that flow) against the real server: sign-in (passkey or password) in the system browser, the
/// handoff that trades the fresh session for a PKCE-bound code, the studylife:// redirect that
/// carries only that code and the app's state, and the app's own exchange with its verifier.
///
/// Device test round 8 (2026-11-17) saw this flow fail with a 401 from api/auth/handoff for both
/// sign-in methods. The server and the current client agree (the tests below), the failing request
/// was the handoff request of the client before the 2026-11-16 audit (M2/M3): token in the body, no
/// session header. That bundle was still precached by the page's service worker after the server
/// moved on - see the last test here and service-worker.published.js.
/// </summary>
public abstract class AppBrowserSignInFlowTestBase(CustomWebApplicationFactory factory)
{
    protected CustomWebApplicationFactory Factory { get; } = factory;

    private const string State = "app-own-state-0123456789";

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The browser's HttpClient as Program.cs builds it, with the token store of a system
    /// browser that has never stored a session (the app flow never does).</summary>
    protected HttpClient BrowserClient()
    {
        var baseAddress = new Uri("http://localhost/");
        var store = new SessionTokenStore(Substitute.For<IJSRuntime>());
        var client = Factory.CreateDefaultClient(baseAddress, new SessionHandler(store, baseAddress, Factory.Services));
        // CreateDefaultClient applies the factory's auto sign-in for user 1; a system browser has none.
        client.DefaultRequestHeaders.Remove(AuthSessionService.TokenHeaderName);
        return client;
    }

    /// <summary>Everything after the sign-in returned its token: the page's handoff, the redirect
    /// back to the app, and the app's exchange of the code. Returns the token the app ends up with.</summary>
    protected async Task<string> HandBackToTheAppAsync(HttpClient browser, string token)
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var page = AppReturnContext.FromUri(new Uri($"https://localhost/login?app=1&appchallenge={challenge}&appstate={State}"));
        Assert.True(page.IsActive);

        var redirect = new Uri(await page.BuildTokenReturnRedirectAsync(browser, token));

        Assert.Equal("studylife", redirect.Scheme);
        Assert.DoesNotContain(token, redirect.ToString());
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal(State, query["state"]);
        Assert.Matches(PkceShape.CodePattern, query["code"]!);

        using var app = ApiKeyTestHelpers.CreateClientWithKey(Factory, null);
        var exchange = await app.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = query["code"]!, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        return (await exchange.Content.ReadFromJsonAsync<AuthExchangeResponseDto>())!.Token;
    }

    protected async Task AssertIsAWorkingSessionAsync(string token)
    {
        using var app = ApiKeyTestHelpers.CreateClientWithKey(Factory, null);
        var whoami = await PasskeyHttp.GetWithTokenAsync(app, "/api/auth/whoami", token);
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        Assert.Equal("session", (await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>())!.Credential);
    }
}

public class AppBrowserPasskeySignInFlowTests(CustomWebApplicationFactory factory)
    : AppBrowserSignInFlowTestBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task PasskeySignIn_InTheSystemBrowser_EndsWithTheSameSessionInTheApp()
    {
        using var passkey = new FakePasskey();
        using var browser = BrowserClient();
        await PasskeyHttp.RegisterAsync(Factory, browser, "Alex", passkey);

        var (optionsId, optionsJson) = await PasskeyHttp.BeginAsync(browser, "/api/auth/login/begin");
        var complete = await PasskeyHttp.CompleteAsync(browser, "/api/auth/login/complete", optionsId,
            passkey.CreateAssertionResponse(optionsJson, PasskeyHttp.Origin, signCount: 5));
        Assert.True(complete.StatusCode == HttpStatusCode.OK, $"login/complete: {complete.StatusCode} {await complete.Content.ReadAsStringAsync()}");
        var token = (await complete.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token;
        Assert.False(string.IsNullOrEmpty(token));

        var appToken = await HandBackToTheAppAsync(browser, token!);

        Assert.Equal(token, appToken);
        await AssertIsAWorkingSessionAsync(appToken);
    }
}

public class AppBrowserPasswordSignInFlowTests(CustomWebApplicationFactory factory)
    : AppBrowserSignInFlowTestBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _password = new(factory);

    [Fact]
    public async Task PasswordSignIn_InTheSystemBrowser_EndsWithTheSameSessionInTheApp()
    {
        await _password.EnsureClaimedAsync();
        using var browser = BrowserClient();
        var login = await browser.PostAsJsonAsync("/api/auth/password/login",
            new PasswordLoginRequestDto { Username = "seed.owner", Password = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token;
        Assert.False(string.IsNullOrEmpty(token));

        var appToken = await HandBackToTheAppAsync(browser, token!);

        Assert.Equal(token, appToken);
        await AssertIsAWorkingSessionAsync(appToken);
    }

    /// <summary>
    /// The request the test instance logged in round 8, byte for byte in shape: the handoff of the
    /// client up to 3.34.13 put the token in a 117-byte body and sent no session header. It stays
    /// refused (an anonymous handoff is exactly what M3 closed); what has to change is that an
    /// app sign-in page never runs that stale bundle (service-worker.published.js).
    /// </summary>
    [Fact]
    public async Task TheHandoffOfAStaleClientBundle_IsRefused()
    {
        await _password.EnsureClaimedAsync();
        using var browser = BrowserClient();
        var token = await _password.LoginTokenAsync("seed.owner", PasswordHttp.Strong);
        var challenge = Base64UrlEncode(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        var legacyBody = $$"""{"token":"{{token}}","codeChallenge":"{{challenge}}"}""";
        Assert.Equal(117, Encoding.UTF8.GetByteCount(legacyBody));

        var handoff = await browser.PostAsync("/api/auth/handoff", new StringContent(legacyBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, handoff.StatusCode);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
