// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>Links for other people or devices use the server's public address, never the
/// NavigationManager base (the native WebView's local app://0.0.0.1/).</summary>
public class ServerLinksTests
{
    private static HttpServerLinks For(string baseAddress) =>
        new(new HttpClient { BaseAddress = new Uri(baseAddress) });

    [Theory]
    [InlineData("https://studylife.example.com/", "register?invite=abc", "https://studylife.example.com/register?invite=abc")]
    [InlineData("https://studylife.example.com", "register?invite=abc", "https://studylife.example.com/register?invite=abc")]
    [InlineData("https://example.com/studylife/", "shared/tok", "https://example.com/studylife/shared/tok")]
    [InlineData("https://example.com/studylife", "shared/tok", "https://example.com/studylife/shared/tok")]
    [InlineData("https://example.com:8443/sl/", "/api/sessions/ics", "https://example.com:8443/sl/api/sessions/ics")]
    public void Absolute_KeepsTheBasePathAndPort(string baseAddress, string relative, string expected)
    {
        Assert.Equal(expected, For(baseAddress).Absolute(relative));
    }

    [Fact]
    public void BaseAddress_NativeServerUrl_IsTheServerNotTheWebViewHost()
    {
        // The native app points the shared HttpClient at the configured server URL.
        var links = For("https://studylife.example.com/");

        Assert.DoesNotContain("app://", links.Absolute("register"));
        Assert.Equal("studylife.example.com", links.BaseAddress.Host);
    }

    [Fact]
    public void Constructor_WithoutBaseAddress_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new HttpServerLinks(new HttpClient()));
    }
}

/// <summary>The components that show a link to share: the WebView's own NavigationManager base is
/// deliberately different from the server address, so any leak of Nav.BaseUri fails here.</summary>
public class ShareLinkComponentTests : ConsentBunitTest
{
    private const string Server = "https://studylife.test/sl/";

    private ScriptedServer Arrange()
    {
        var server = new ScriptedServer();
        AiConsentFixtures.Register(Ctx, server);
        // Replace the plain HttpClient registration's base with a sub-path server.
        Services.AddSingleton<IServerLinks>(new HttpServerLinks(new HttpClient { BaseAddress = new Uri(Server) }));
        Ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("app://0.0.0.1/");
        return server;
    }

    [Fact]
    public void InviteLink_UsesTheServerAddressWithItsSubPath()
    {
        var server = Arrange();
        server.OnJson("GET api/auth/invites", new List<InviteListItemDto>());
        server.OnJson("POST api/auth/invites", new CreateInviteResponseDto { Id = 1, Token = "a b" });
        var cut = Render<SetupInvitesCard>();
        cut.WaitForElement("button").Click();

        var link = cut.WaitForElement("input.input").GetAttribute("value");

        Assert.Equal("https://studylife.test/sl/register?invite=a%20b", link);
    }

    [Fact]
    public void ProgressShareLink_UsesTheServerAddress()
    {
        var server = Arrange();
        server.OnJson("GET api/settings", new UserSettingsDto { ProgressShareEnabled = true, ProgressShareToken = "tok" });
        var cut = Render<SetupProgressShareCard>();

        var link = cut.WaitForElement("input.input").GetAttribute("value");

        Assert.Equal("https://studylife.test/sl/shared/tok", link);
    }
}
