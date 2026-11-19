// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>What the native app shows or hides for store review: no browser-only install tip or
/// server-log advice inside the installed app, a privacy policy link before sign-in, and no
/// developer/marketplace cards where the platform opts out.</summary>
public class StoreReviewVisibilityTests : ConsentBunitTest
{
    private sealed class NativeAuth : INativeAppAuth
    {
        public bool IsAvailable => true;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
    }

    private sealed class StorePlatform : IClientPlatform
    {
        public string Name => "ios";
        public bool ShowsDeveloperIntegrations => false;
    }

    private IRenderedComponent<Register> RenderRegister(INativeAppAuth auth)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = false });
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(new StubJSRuntime()));
        Services.AddSingleton(auth);
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<Register>();
        cut.WaitForAssertion(() => cut.Find("#register-name"));
        return cut;
    }

    [Fact]
    public void Browser_registration_keeps_the_install_tip_and_the_setup_code_hint()
    {
        var cut = RenderRegister(new NoNativeAppAuth());
        cut.WaitForAssertion(() => Assert.Contains("InstallHint", cut.Markup));
        Assert.Contains("SetupSecretHint", cut.Markup);
    }

    [Fact]
    public void Native_registration_hides_the_install_tip_and_the_setup_code_hint()
    {
        var cut = RenderRegister(new NativeAuth());
        cut.WaitForAssertion(() => Assert.Contains("RegisterButton", cut.Markup));
        Assert.DoesNotContain("InstallHint", cut.Markup);
        Assert.DoesNotContain("SetupSecretHint", cut.Markup);
    }

    [Fact]
    public void Public_layout_links_the_privacy_policy_and_imprint_under_every_page()
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddI18nText();
        var server = new ScriptedServer().OnJson("GET api/instance", new InstanceInfoDto { Id = "x" });
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });

        var cut = Render<StudyLife.Client.Shared.PublicLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"page\">content</p>"))));

        cut.Find("#page");
        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.Find("footer.public-legal").QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
            Assert.StartsWith("https://legal.lktec.org/", hrefs[0]);
            Assert.Equal(LegalLinks.PublisherImprint, hrefs[1]);
        });
    }

    [Fact]
    public void Privacy_policy_note_links_the_policy()
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddI18nText();
        var server = new ScriptedServer().OnJson("GET api/instance", new InstanceInfoDto { Id = "x" });
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        var cut = Render<PrivacyPolicyNote>();
        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
            Assert.StartsWith("https://legal.lktec.org/", hrefs[0]);
            Assert.Equal(LegalLinks.PublisherImprint, hrefs[1]);
        });
    }

    [Fact]
    public void Privacy_policy_note_shows_the_operator_policy_next_to_the_app_policy()
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddI18nText();
        var server = new ScriptedServer().OnJson("GET api/instance", new InstanceInfoDto
        {
            Id = "x",
            Legal = new LegalLinksDto { PrivacyUrl = "https://op.example.org/privacy" },
        });
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        var cut = Render<PrivacyPolicyNote>();
        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
            Assert.Equal(["https://op.example.org/privacy"], hrefs.Take(1));
            Assert.StartsWith("https://legal.lktec.org/", hrefs[1]);
            Assert.Equal(LegalLinks.PublisherImprint, hrefs[2]);
        });
    }

    [Fact]
    public void The_browser_shows_the_developer_integrations_and_a_store_shell_can_hide_them()
    {
        IClientPlatform browser = new BrowserClientPlatform();
        IClientPlatform store = new StorePlatform();
        Assert.True(browser.ShowsDeveloperIntegrations);
        Assert.False(store.ShowsDeveloperIntegrations);
    }
}
