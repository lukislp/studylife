// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.I18nText;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>Which legal addresses the client links (publisher defaults, operator overrides).</summary>
public class LegalLinksTests
{
    [Fact]
    public void Without_operator_settings_it_is_the_publishers_policy_and_imprint()
    {
        var links = LegalLinks.Resolve("en", new LegalLinksDto());

        Assert.False(links.HasOperatorPrivacy);
        Assert.Equal(PrivacyPolicyLink.English, links.AppPrivacyUrl);
        Assert.Equal(LegalLinks.PublisherImprint, links.ImprintUrl);
    }

    [Fact]
    public void Operator_privacy_url_is_separate_from_the_app_policy()
    {
        var links = LegalLinks.Resolve("de", new LegalLinksDto { PrivacyUrl = "https://op.example.org/privacy" });

        Assert.True(links.HasOperatorPrivacy);
        Assert.Equal("https://op.example.org/privacy", links.OperatorPrivacyUrl);
        Assert.Equal(PrivacyPolicyLink.German, links.AppPrivacyUrl);
        Assert.Equal(LegalLinks.PublisherImprint, links.ImprintUrl);
    }

    [Fact]
    public void Operator_imprint_wins_over_the_publishers()
    {
        var links = LegalLinks.Resolve("en", new LegalLinksDto { ImprintUrl = "https://op.example.org/imprint" });

        Assert.Equal("https://op.example.org/imprint", links.ImprintUrl);
        Assert.False(links.HasOperatorPrivacy);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://evil.example.org/")]
    [InlineData("not a url")]
    public void An_unusable_address_in_the_answer_is_ignored(string url)
    {
        var links = LegalLinks.Resolve("en", new LegalLinksDto { PrivacyUrl = url, ImprintUrl = url });

        Assert.False(links.HasOperatorPrivacy);
        Assert.Equal(LegalLinks.PublisherImprint, links.ImprintUrl);
    }

    [Fact]
    public async Task Source_reads_api_instance()
    {
        var server = new ScriptedServer().OnJson("GET api/instance", new InstanceInfoDto
        {
            Id = "x",
            Legal = new LegalLinksDto { PrivacyUrl = "https://op.example.org/p", ImprintUrl = "https://op.example.org/i" },
        });
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };

        var links = await new LegalLinksSource(http).LoadAsync("en");

        Assert.Equal("https://op.example.org/p", links.OperatorPrivacyUrl);
        Assert.Equal("https://op.example.org/i", links.ImprintUrl);
    }

    [Fact]
    public async Task Source_falls_back_to_the_publisher_when_the_server_cannot_answer()
    {
        var offline = new ScriptedServer().On("GET api/instance", _ => throw new HttpRequestException("offline"));
        var portal = new ScriptedServer().On("GET api/instance",
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>portal</html>") });
        var missing = new ScriptedServer().On("GET api/instance", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        foreach (var server in new[] { offline, portal, missing })
        {
            var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
            var links = await new LegalLinksSource(http).LoadAsync("en");
            Assert.Equal(LegalLinks.Publisher("en"), links);
        }
    }
}

/// <summary>The About card and the shared anchors render the imprint and the operator/app split.</summary>
public class LegalLinksRenderingTests : BunitContext
{
    private static readonly AboutText Texts = new()
    {
        Heading = "About",
        Desc = "d",
        ServerLabel = "Server",
        BrowserLabel = "Browser",
        AppLabel = "App",
        Unknown = "unknown",
        SourceLink = "Source code",
        SourceExact = "Exact",
        SourceRepo = "Repo",
        NewTab = "opens in a new tab",
        LicenceLabel = "Licence",
        LicenceMeaning = "m",
        PrivacyPolicy = "Privacy policy",
        Imprint = "Imprint",
        OperatorPrivacyPolicy = "Operator privacy policy",
        AppPrivacyPolicy = "App privacy policy",
        NoticesHeading = "Notices",
        NoticesDesc = "n",
        ShowNotices = "Show",
        HideNotices = "Hide",
        Loading = "Loading",
        NoticesFailed = "failed",
        FromApp = "app",
        FromServer = "server",
    };

    private IRenderedComponent<SetupAboutCard> RenderCard(LegalLinks legal)
    {
        var server = new ScriptedServer().OnJson("GET api/system/version", new VersionResponseDto { Version = "3.26.5" });
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        Services.AddSingleton(http);
        Services.AddSingleton<IClientPlatform>(new FakePlatform());
        var state = new AboutState(http, new FakePlatform(), "3.26.5");
        return Render<SetupAboutCard>(p => p.Add(c => c.Text, Texts).Add(c => c.State, state).Add(c => c.Legal, legal));
    }

    private static void AssertExternal(AngleSharp.Dom.IElement link, string href)
    {
        Assert.Equal(href, link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"));
    }

    [Fact]
    public void About_with_defaults_shows_one_privacy_link_and_the_imprint()
    {
        var cut = RenderCard(LegalLinks.Publisher("en"));

        var links = cut.FindAll("a").ToDictionary(a => a.TextContent.Trim());
        AssertExternal(links["Privacy policy"], PrivacyPolicyLink.English);
        AssertExternal(links["Imprint"], LegalLinks.PublisherImprint);
        Assert.DoesNotContain("Operator privacy policy", cut.Markup);
        Assert.DoesNotContain("App privacy policy", cut.Markup);
    }

    [Fact]
    public void About_with_an_operator_policy_splits_it_from_the_app_policy()
    {
        var cut = RenderCard(LegalLinks.Resolve("en", new LegalLinksDto
        {
            PrivacyUrl = "https://op.example.org/privacy",
            ImprintUrl = "https://op.example.org/imprint",
        }));

        var links = cut.FindAll("a").ToDictionary(a => a.TextContent.Trim());
        AssertExternal(links["Operator privacy policy"], "https://op.example.org/privacy");
        AssertExternal(links["App privacy policy"], PrivacyPolicyLink.English);
        AssertExternal(links["Imprint"], "https://op.example.org/imprint");
        Assert.False(links.ContainsKey("Privacy policy"));
    }

    [Fact]
    public void Anchors_component_renders_imprint_beside_the_privacy_link()
    {
        var cut = Render<LegalLinkAnchors>(p => p
            .Add(c => c.Texts, Texts)
            .Add(c => c.Links, LegalLinks.Publisher("en")));

        Assert.Equal(["Privacy policy", "Imprint"], cut.FindAll("a").Select(a => a.TextContent.Trim()));
    }
}
