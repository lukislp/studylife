// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>2026-11-16 audit, M2: the auth pages hand a sign-in back to a native app only through the
/// PKCE code, only when the app sent a valid challenge, and echo the app's own state.</summary>
public class AppReturnContextHandoffTests
{
    // RFC 7636 appendix B example challenge (43 base64url characters).
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string State = "q1w2e3r4t5y6u7i8o9p0";

    private static AppReturnContext Parse(string query) =>
        AppReturnContext.FromUri(new Uri("https://studylife.example.com/login?" + query));

    [Theory]
    [InlineData("app=1")]
    [InlineData("app=1&appchallenge=abc")]
    [InlineData("app=1&appchallenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c%2B")]
    [InlineData("app=1&appret=http%3A%2F%2F127.0.0.1%3A5000%2F")]
    public void WithoutAValidChallenge_ItIsNotAnAppFlow(string query)
    {
        var context = Parse(query);

        Assert.False(context.IsActive);
        Assert.Equal("", context.LinkQuery);
    }

    [Fact]
    public async Task WithoutAValidChallenge_NoTokenCanBeHandedBack()
    {
        var context = Parse("app=1");
        using var http = new HttpClient(new ScriptedServer()) { BaseAddress = new Uri("https://studylife.test/") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.BuildTokenReturnRedirectAsync(http, "session-token"));
    }

    [Fact]
    public void TheAppsState_IsCarriedAcrossPages_AndEchoedInEveryReturnRedirect()
    {
        var context = Parse($"app=1&appchallenge={Challenge}&appstate={State}");

        Assert.True(context.IsActive);
        Assert.Contains($"appstate={State}", context.LinkQuery);
        Assert.Equal($"studylife://auth?linked=1&state={State}", context.BuildReturnRedirect("linked=1"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has%20space%20aaaaaaaaaaaaaaaaaa")]
    public void AMalformedState_IsDropped(string state)
    {
        var context = Parse($"app=1&appchallenge={Challenge}&appstate={state}");

        Assert.True(context.IsActive);
        Assert.DoesNotContain("appstate", context.LinkQuery);
        Assert.Equal("studylife://auth?linked=1", context.BuildReturnRedirect("linked=1"));
    }

    [Fact]
    public async Task TheToken_GoesToTheServerAsTheSessionHeader_AndOnlyTheCodeGoesToTheApp()
    {
        var server = new ScriptedServer();
        string? sentHeader = null;
        server.On("POST api/auth/handoff", request =>
        {
            sentHeader = request.Headers.GetValues("X-Session-Token").Single();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthHandoffResponseDto { Code = "the-code" }) };
        });
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        var context = Parse($"app=1&appchallenge={Challenge}&appstate={State}&appret=http%3A%2F%2F127.0.0.1%3A5000%2F");

        var redirect = await context.BuildTokenReturnRedirectAsync(http, "session-token");

        Assert.Equal($"http://127.0.0.1:5000/?code=the-code&state={State}", redirect);
        Assert.DoesNotContain("session-token", redirect);
        Assert.Equal("session-token", sentHeader);
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal(Challenge, body["codeChallenge"]!.GetValue<string>());
        Assert.False(body.ContainsKey("token"));
    }

    [Fact]
    public void ALoopbackReturn_IsTheOnlyAcceptedReturnUrl()
    {
        var context = Parse($"app=1&appchallenge={Challenge}&appret=https%3A%2F%2Fevil.example%2F");

        Assert.Equal("studylife://auth?linked=1", context.BuildReturnRedirect("linked=1"));
    }
}
