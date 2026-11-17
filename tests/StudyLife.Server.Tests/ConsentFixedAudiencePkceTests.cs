// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Audit M5: PKCE on the five built-in (fixed-audience) consent flows. An offered S256 challenge
/// is validated, bound to the assertion and enforced at exchange; an absent one is tolerated only
/// while Consent:RequirePkce is false. Dynamic clients (always PKCE) live in
/// GenericOAuthConnectPkceTests.
/// </summary>
public class ConsentFixedAudiencePkceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ConsentFixedAudiencePkceTests(CustomWebApplicationFactory factory) => _factory = factory;

    // The redirects every built-in audience really uses, taken from ConsentRedirectCatalogue so a
    // change to the pinned IDs or the loopback path cannot leave these tests on stale values. Both
    // focus IDs (store and unpacked) are covered, one per focus audience.
    private static string ChromeRedirect(string extensionId) => $"https://{extensionId}.chromiumapp.org/";

    public static TheoryData<string, string> Audiences => new()
    {
        { "mcp", "https://mcp.example.com/auth/studylife/callback" },
        { "capture", ChromeRedirect(ConsentRedirectCatalogue.CaptureStoreExtensionId) },
        { "focusguard", ChromeRedirect(ConsentRedirectCatalogue.FocusStoreExtensionId) },
        { "focustunes", ChromeRedirect(ConsentRedirectCatalogue.FocusUnpackedExtensionId) },
        { "tray", $"http://127.0.0.1:41999{ConsentRedirectCatalogue.LoopbackCallbackPath}" },
    };

    private static string OtherVerifier => "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static Task<HttpResponseMessage> ConnectAsync(HttpClient session, string audience, string redirect, string? challenge, string? method) =>
        session.PostAsJsonAsync($"/api/auth/{audience}-connect",
            new { redirectUri = redirect, state = "s", codeChallenge = challenge, codeChallengeMethod = method });

    private async Task<string> AssertionAsync(HttpClient session, string audience, string redirect, string? challenge, string? method = "S256")
    {
        var response = await ConnectAsync(session, audience, redirect, challenge, challenge is null ? null : method);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var redirectTo = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["redirectTo"];
        return System.Web.HttpUtility.ParseQueryString(new Uri(redirectTo).Query)["assertion"]!;
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(WebApplicationFactory<Program> factory, string audience, string assertion, string? verifier)
    {
        using var anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Remove("X-Api-Key");
        anon.DefaultRequestHeaders.Remove(AuthSessionService.TokenHeaderName);
        return await anon.PostAsJsonAsync($"/api/auth/{audience}-assertion-exchange", new { assertion, codeVerifier = verifier });
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task OfferedChallenge_CorrectVerifierRedeems(string audience, string redirect)
    {
        var assertion = await AssertionAsync(_factory.CreateClient(), audience, redirect, TestPkce.Challenge);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(_factory, audience, assertion, TestPkce.Verifier)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task OfferedChallenge_WrongOrMissingVerifier_Is401_AndStaysRedeemable(string audience, string redirect)
    {
        var assertion = await AssertionAsync(_factory.CreateClient(), audience, redirect, TestPkce.Challenge);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(_factory, audience, assertion, OtherVerifier)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(_factory, audience, assertion, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(_factory, audience, assertion, TestPkce.Verifier)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task HalfAPair_Is400(string audience, string redirect)
    {
        var session = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync(session, audience, redirect, TestPkce.Challenge, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync(session, audience, redirect, null, "S256")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task PlainOrMalformed_Is400(string audience, string redirect)
    {
        var session = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync(session, audience, redirect, TestPkce.Challenge, "plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync(session, audience, redirect, "short", "S256")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task NoChallenge_WorksWhileRequirePkceIsOff(string audience, string redirect)
    {
        var assertion = await AssertionAsync(_factory.CreateClient(), audience, redirect, null);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(_factory, audience, assertion, null)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task NoChallenge_Is400_WhenRequirePkceIsOn(string audience, string redirect)
    {
        using var strict = _factory.WithWebHostBuilder(b => b.UseSetting("Consent:RequirePkce", "true"));
        var session = strict.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await ConnectAsync(session, audience, redirect, null, null)).StatusCode);
        // ...and an offered challenge still works with the switch on.
        var assertion = await AssertionAsync(session, audience, redirect, TestPkce.Challenge);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(strict, audience, assertion, TestPkce.Verifier)).StatusCode);
    }
}
