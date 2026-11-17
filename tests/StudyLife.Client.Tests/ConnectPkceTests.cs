// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>PKCE on the consent pages (audit M5). Texts render as their keys, see
/// AiConsentFixtures.Register.</summary>
public class ConnectPkceTests : ConsentBunitTest
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string Redirect = "https://mcp.example.com/auth/studylife/callback";

    private async Task<(ScriptedServer Server, NavigationManager Nav)> SetUpAsync(string url, bool signedIn)
    {
        var server = new ScriptedServer();
        server.OnJson("POST api/auth/mcp-connect", new McpConnectResponseDto { RedirectTo = Redirect + "?assertion=a&state=s" });
        server.OnJson("GET api/auth/oauth-clients/c1", new OAuthClientInfoDto { Name = "C", Description = "d" });
        var store = new SessionTokenStore(new StubJSRuntime());
        if (signedIn) await store.SetTokenAsync("session-token");
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(store);
        Services.AddI18nText();
        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        nav.NavigateTo(url);
        return (server, nav);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    [Fact]
    public async Task ConnectConsent_ForwardsTheChallengeInThePost()
    {
        var (server, _) = await SetUpAsync($"/connect/mcp?redirect_uri={Q(Redirect)}&state=s&code_challenge={Challenge}&code_challenge_method=S256", signedIn: true);
        var cut = Render<ConnectConsent>(p => p.Add(c => c.Audience, "mcp"));

        cut.WaitForAssertion(() => cut.Find("button.btn-primary"), ChainTimeout);
        cut.Find("button.btn-primary").Click();
        cut.WaitForState(() => server.Count("POST api/auth/mcp-connect") > 0, ChainTimeout);

        var body = Assert.Single(server.Bodies);
        Assert.Contains($"\"codeChallenge\":\"{Challenge}\"", body);
        Assert.Contains("\"codeChallengeMethod\":\"S256\"", body);
    }

    [Fact]
    public async Task ConnectConsent_CarriesTheChallengeThroughTheLoginBounce()
    {
        var (_, nav) = await SetUpAsync($"/connect/mcp?redirect_uri={Q(Redirect)}&state=s&code_challenge={Challenge}&code_challenge_method=S256", signedIn: false);
        var cut = Render<ConnectConsent>(p => p.Add(c => c.Audience, "mcp"));
        cut.WaitForState(() => nav.Uri.Contains("/login"), ChainTimeout);

        var returnUrl = System.Web.HttpUtility.ParseQueryString(new Uri(nav.Uri).Query)["returnUrl"];
        Assert.NotNull(returnUrl);
        Assert.StartsWith("/login", new Uri(nav.Uri).AbsolutePath);
        Assert.Contains($"code_challenge={Challenge}", returnUrl);
        Assert.Contains("code_challenge_method=S256", returnUrl);
    }

    [Theory]
    [InlineData("code_challenge=" + Challenge)]
    [InlineData("code_challenge_method=S256")]
    public async Task ConnectConsent_HalfAPair_IsAnInvalidRequest(string half)
    {
        await SetUpAsync($"/connect/mcp?redirect_uri={Q(Redirect)}&state=s&{half}", signedIn: true);
        var cut = Render<ConnectConsent>(p => p.Add(c => c.Audience, "mcp"));

        cut.WaitForAssertion(() => Assert.Contains("ConnectInvalidRequest", cut.Markup), ChainTimeout);
        Assert.Empty(cut.FindAll("button.btn-primary"));
    }

    [Fact]
    public async Task ConnectClient_WithoutAChallenge_IsAnInvalidRequest()
    {
        await SetUpAsync($"/connect/client/c1?redirect_uri={Q(Redirect)}&state=s", signedIn: true);
        var cut = Render<ConnectClient>(p => p.Add(c => c.ClientId, "c1"));

        cut.WaitForAssertion(() => Assert.Contains("ConnectInvalidRequest", cut.Markup), ChainTimeout);
    }

    [Fact]
    public async Task ConnectClient_WithAChallenge_RendersTheConsent()
    {
        await SetUpAsync($"/connect/client/c1?redirect_uri={Q(Redirect)}&state=s&code_challenge={Challenge}&code_challenge_method=S256", signedIn: true);
        var cut = Render<ConnectClient>(p => p.Add(c => c.ClientId, "c1"));

        cut.WaitForAssertion(() => cut.Find("button.btn-primary"), ChainTimeout);
        Assert.DoesNotContain("ConnectInvalidRequest", cut.Markup);
    }
}
