// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using static StudyLife.Client.Tests.AiConsentFixtures;

namespace StudyLife.Client.Tests;

/// <summary>How a marketplace listing is installed is decided by its explicit "install" field.</summary>
public class MarketplaceListingDtoTests
{
    private static MarketplaceListingDto Parse(string json) =>
        JsonSerializer.Deserialize<MarketplaceListingDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void ListingWithoutInstallField_IsOneClick_LikeBefore()
    {
        var listing = Parse("""{"id":"a","redirectUriPattern":"https://a.example/cb"}""");

        Assert.False(listing.RequiresManualSetup);
        Assert.True(listing.CanInstallOneClick);
    }

    [Fact]
    public void ExplicitOauthListing_IsOneClick()
    {
        var listing = Parse("""{"id":"a","install":"oauth","redirectUriPattern":"https://a.example/cb"}""");

        Assert.False(listing.RequiresManualSetup);
        Assert.True(listing.CanInstallOneClick);
    }

    [Fact]
    public void ManualListing_NeedsManualSetup_AndNeverOffersOneClick()
    {
        var listing = Parse("""{"id":"a","install":"manual","setupUrl":"https://a.example/setup"}""");

        Assert.True(listing.RequiresManualSetup);
        Assert.False(listing.CanInstallOneClick);
        Assert.Equal("https://a.example/setup", listing.SafeSetupUrl);
    }

    [Fact]
    public void ManualListing_NeverOffersOneClick_EvenIfItCarriesARedirectUri()
    {
        var listing = Parse("""{"id":"a","install":"manual","redirectUriPattern":"https://a.example/cb"}""");

        Assert.False(listing.CanInstallOneClick);
    }

    [Theory]
    [InlineData("""{"id":"a","install":"future-mode","redirectUriPattern":"https://a.example/cb"}""")]
    [InlineData("""{"id":"a"}""")]
    public void UnknownInstallModeOrMissingRedirectUri_OffersNoOneClick(string json)
    {
        Assert.False(Parse(json).CanInstallOneClick);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("/relative/path")]
    [InlineData("//evil.example/x")]
    [InlineData("https://")]
    [InlineData("")]
    [InlineData(null)]
    public void SetupUrl_ThatIsNotAnHttpLink_IsDropped(string? url)
    {
        Assert.Null(new MarketplaceListingDto { Install = "manual", SetupUrl = url }.SafeSetupUrl);
    }

    [Fact]
    public void SetupUrl_WithHttps_IsKept()
    {
        Assert.Equal("https://a.example/setup#x", new MarketplaceListingDto { SetupUrl = "https://a.example/setup#x" }.SafeSetupUrl);
    }
}

/// <summary>The marketplace modal: manual listings show setup steps instead of an Install button.</summary>
public class MarketplaceBrowserModalTests : ConsentBunitTest
{
    private IRenderedComponent<MarketplaceBrowserModal> RenderWith(params object[] listings)
    {
        var server = new ScriptedServer()
            .OnJson("GET api/auth/demo", new { demo = false })
            .OnJson("GET repos/lukislp/studylife-marketplace/contents/listings",
                listings.Select((_, i) => new { name = $"l{i}.json", download_url = $"https://raw.test/l{i}.json" }).ToArray());
        for (var i = 0; i < listings.Length; i++)
            server.OnJson($"GET l{i}.json", listings[i]);

        Register(Ctx, server);
        Services.AddSingleton(new MarketplaceClient(new HttpClient(server) { BaseAddress = new Uri("https://api.github.test/") }, Ctx.JSInterop.JSRuntime));
        var cut = Render<MarketplaceBrowserModal>();
        cut.WaitForAssertion(() => Assert.Contains("MarketplaceScopesLabel", cut.Markup), ChainTimeout);
        return cut;
    }

    private static object Listing(string id, object? extra = null) => new Dictionary<string, object?>
    {
        ["id"] = id, ["name"] = id, ["description"] = "d", ["developer"] = "dev",
        ["repository"] = "https://github.com/x/" + id, ["requestedScopes"] = new[] { "Notes.Create" },
    }.Concat(extra is null ? [] : extra.GetType().GetProperties().Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(extra))))
     .ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void OauthListing_ShowsInstallButton_ManualListingShowsStepsAndLinkInstead()
    {
        var cut = RenderWith(
            Listing("one-click", new { redirectUriPattern = "https://a.example/cb" }),
            Listing("self-hosted", new { install = "manual", setupUrl = "https://a.example/setup" }));

        Assert.Single(cut.FindAll("button.btn-secondary"));
        Assert.Contains("MarketplaceInstallButton", cut.Markup);
        Assert.Contains("MarketplaceManualSetupNotice", cut.Markup);
        Assert.Equal("https://a.example/setup", cut.Find("a[href='https://a.example/setup']").GetAttribute("href"));
    }

    [Fact]
    public void ManualListing_WithUnsafeSetupUrl_ShowsNoLink()
    {
        var cut = RenderWith(Listing("self-hosted", new { install = "manual", setupUrl = "javascript:alert(1)" }));

        Assert.Empty(cut.FindAll("button.btn-secondary"));
        Assert.DoesNotContain("javascript:", cut.Markup);
        Assert.DoesNotContain("MarketplaceManualSetupLink", cut.Markup);
    }
}
