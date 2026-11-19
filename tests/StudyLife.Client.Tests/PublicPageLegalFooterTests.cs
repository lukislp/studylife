// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The real public pages inside PublicLayout (the layout their @layout attribute selects):
/// the privacy policy and imprint links are there with and without operator Legal:* settings. A
/// page rendered on its own would not show the footer, so the pages go through a LayoutView.</summary>
public class PublicPageLegalFooterTests : ConsentBunitTest
{
    private sealed class NoNativeAuthStub : INativeAppAuth
    {
        public bool IsAvailable => false;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
    }

    private ScriptedServer NewServer(LegalLinksDto? legal)
    {
        var server = new ScriptedServer()
            .OnJson("GET api/instance", new InstanceInfoDto { Id = "x", Legal = legal ?? new LegalLinksDto() })
            .OnJson("GET api/auth/demo", new DemoInfoDto { Demo = false })
            .OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = false });
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(new StubJSRuntime()));
        Services.AddSingleton<INativeAppAuth>(new NoNativeAuthStub());
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return server;
    }

    private IRenderedComponent<LayoutView> RenderInLayout<TPage>(Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent =>
        Render<LayoutView>(p => p
            .Add(c => c.Layout, typeof(PublicLayout))
            .AddChildContent<TPage>(parameters));

    private List<(string Href, string Text)> FooterLinks(IRenderedComponent<LayoutView> cut)
    {
        var links = new List<(string, string)>();
        cut.WaitForAssertion(() =>
        {
            links = cut.Find("footer.public-legal").QuerySelectorAll("a")
                .Select(a => (a.GetAttribute("href")!, a.TextContent.Trim())).ToList();
            Assert.NotEmpty(links);
        }, ChainTimeout);
        return links;
    }

    private static void AssertPublisherDefaults(List<(string Href, string Text)> links)
    {
        Assert.Equal(2, links.Count);
        Assert.Contains(links[0].Href, new[] { PrivacyPolicyLink.German, PrivacyPolicyLink.English });
        Assert.Equal(LegalLinks.PublisherImprint, links[1].Href);
        Assert.Equal("PublisherImprint", links[1].Text); // the publisher label, not the plain "Imprint" (text keys stand in for the table here)
    }

    private static void AssertOperatorLinks(List<(string Href, string Text)> links)
    {
        Assert.Equal(3, links.Count);
        Assert.Equal("https://op.example.org/privacy", links[0].Href);
        Assert.Contains(links[1].Href, new[] { PrivacyPolicyLink.German, PrivacyPolicyLink.English });
        Assert.Equal("https://op.example.org/imprint", links[2].Href);
    }

    private static readonly LegalLinksDto Operator = new()
    {
        PrivacyUrl = "https://op.example.org/privacy",
        ImprintUrl = "https://op.example.org/imprint",
    };

    [Fact]
    public void Shared_progress_page_links_the_publisher_policy_and_imprint_without_operator_config()
    {
        NewServer(null);
        var cut = RenderInLayout<SharedProgress>(p => p.Add(c => c.Token, "tok"));
        AssertPublisherDefaults(FooterLinks(cut));
    }

    [Fact]
    public void Shared_progress_page_links_the_operator_pages_when_configured()
    {
        NewServer(Operator);
        var cut = RenderInLayout<SharedProgress>(p => p.Add(c => c.Token, "tok"));
        AssertOperatorLinks(FooterLinks(cut));
    }

    [Fact]
    public void Login_page_links_the_publisher_policy_and_imprint_without_operator_config()
    {
        NewServer(null);
        var cut = RenderInLayout<Login>();
        AssertPublisherDefaults(FooterLinks(cut));
    }

    [Fact]
    public void Login_page_links_the_operator_pages_when_configured()
    {
        NewServer(Operator);
        var cut = RenderInLayout<Login>();
        AssertOperatorLinks(FooterLinks(cut));
    }

    [Fact]
    public void The_footer_stays_when_api_instance_fails()
    {
        var server = NewServer(null);
        server.On("GET api/instance", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var cut = RenderInLayout<Login>();
        AssertPublisherDefaults(FooterLinks(cut));
    }
}
