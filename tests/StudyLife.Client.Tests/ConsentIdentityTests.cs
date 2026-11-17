// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>
/// The consent screens say who receives the key (2026-11-16 audit M5): the add-on, the host the
/// key is handed to and who registered it - and an add-on the owner has not approved cannot be
/// connected from there. Texts render as their keys (see AiConsentFixtures.Register).
/// </summary>
public class ConsentIdentityTests : ConsentBunitTest
{
    // RFC 7636 appendix B challenge: valid PKCE on every request, so these hold with or without
    // a consent page that insists on it.
    private const string Pkce = "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

    private async Task<ScriptedServer> SignedInAsync(string url)
    {
        var server = new ScriptedServer();
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        await tokens.SetTokenAsync("session-token");
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(tokens);
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>().NavigateTo(url);
        return server;
    }

    private static OAuthClientInfoDto Info(string approval = OAuthClientApprovalStates.Approved, string? registeredBy = "Mallory", bool byOwner = false) => new()
    {
        Name = "Sync Helper",
        Description = "Syncs notes.",
        RequestedScopes = new List<string> { "Notes.GetAll" },
        RegisteredBy = registeredBy,
        RegisteredByOwner = byOwner,
        Approval = approval,
        RedirectUris = new List<string> { "https://sync.example.com/cb" },
    };

    private async Task<IRenderedComponent<ConnectClient>> RenderClientAsync(OAuthClientInfoDto info)
    {
        var server = await SignedInAsync("/connect/client/sync-helper?redirect_uri=https%3A%2F%2Fsync.example.com%3A8443%2Fcb&state=s" + Pkce);
        server.OnJson("GET api/auth/oauth-clients/sync-helper", info);
        var cut = Render<ConnectClient>(p => p.Add(c => c.ClientId, "sync-helper"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=consent-registrant]"), ChainTimeout);
        return cut;
    }

    [Fact]
    public async Task ApprovedClient_ShowsNameRedirectHostRegistrantAndScopes()
    {
        var cut = await RenderClientAsync(Info());

        Assert.Contains("Sync Helper", cut.Markup);
        var host = cut.Find("[data-testid=consent-redirect-host]");
        Assert.Contains("ConnectKeyRecipient", host.TextContent);
        Assert.Contains("sync.example.com:8443", host.TextContent);
        Assert.Equal("ConnectRegisteredBy", cut.Find("[data-testid=consent-registrant]").TextContent.Trim());
        Assert.Contains("ConnectRequestedAccess", cut.Markup);
        Assert.NotEmpty(cut.FindAll("button.btn-primary"));
    }

    [Theory]
    [InlineData("Owner", true, "ConnectRegisteredByOwner")]
    [InlineData(null, false, "ConnectRegistrantUnknown")]
    public async Task Registrant_IsNamedForTheOwnerAndForADeletedAccount(string? registeredBy, bool byOwner, string expectedKey)
    {
        var cut = await RenderClientAsync(Info(registeredBy: registeredBy, byOwner: byOwner));

        Assert.Equal(expectedKey, cut.Find("[data-testid=consent-registrant]").TextContent.Trim());
    }

    [Theory]
    [InlineData(OAuthClientApprovalStates.Pending, "ConnectClientPendingApproval")]
    [InlineData(OAuthClientApprovalStates.Rejected, "ConnectClientRejected")]
    public async Task UnapprovedClient_OffersNoConnectButton(string approval, string expectedKey)
    {
        var cut = await RenderClientAsync(Info(approval));

        Assert.Equal(expectedKey, cut.Find("[data-testid=consent-not-approved]").TextContent.Trim());
        Assert.Empty(cut.FindAll("button.btn-primary"));
    }

    [Fact]
    public async Task BuiltInAudience_SaysItShipsWithStudyAtrium()
    {
        await SignedInAsync("/connect/tray?redirect_uri=http%3A%2F%2F127.0.0.1%3A51234%2Fcallback&state=s" + Pkce);

        var cut = Render<ConnectConsent>(p => p.Add(c => c.Audience, "tray"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=consent-registrant]"), ChainTimeout);

        Assert.Equal("ConnectBuiltInClient", cut.Find("[data-testid=consent-registrant]").TextContent.Trim());
    }
}

/// <summary>The owner's add-on approvals card.</summary>
public class SetupClientReviewCardTests : ConsentBunitTest
{
    private static OAuthClientReviewDto Row(string approval) => new()
    {
        ClientId = "sync-helper",
        Name = "Sync Helper",
        RegisteredBy = "Mallory",
        Approval = approval,
        RedirectUris = new List<string> { "https://sync.example.com/cb", "https://sync.example.com/other" },
        RequestedScopes = new List<string> { "Notes.GetAll" },
    };

    [Fact]
    public void PendingRow_ShowsRegistrantAndHost_AndApprovingUpdatesIt()
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/oauth-client-reviews", new List<OAuthClientReviewDto> { Row(OAuthClientApprovalStates.Pending) });
        server.OnJson("POST api/oauth-client-reviews/sync-helper/approve", Row(OAuthClientApprovalStates.Approved));
        AiConsentFixtures.Register(Ctx, server);

        var cut = Render<SetupClientReviewCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=client-review-row]"), ChainTimeout);
        Assert.Contains("ClientReviewPendingLabel", cut.Markup);
        Assert.Contains("ClientReviewRegisteredBy", cut.Markup);
        Assert.Contains("ClientReviewRedirectHosts", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent.Contains("ClientReviewApprove")).Click();

        cut.WaitForAssertion(() => Assert.Contains("ClientReviewApprovedLabel", cut.Markup), ChainTimeout);
        Assert.Equal(1, server.Count("POST api/oauth-client-reviews/sync-helper/approve"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("ClientReviewApprove"));
    }

    [Fact]
    public void NothingToReview_ShowsTheEmptyState()
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/oauth-client-reviews", new List<OAuthClientReviewDto>());
        AiConsentFixtures.Register(Ctx, server);

        var cut = Render<SetupClientReviewCard>();

        cut.WaitForAssertion(() => Assert.Contains("ClientReviewEmpty", cut.Markup), ChainTimeout);
    }
}

/// <summary>Marketplace Install only binds to a registration that is exactly the listing's add-on.</summary>
public class MarketplaceRegistrationMatchTests
{
    private static MarketplaceListingDto Listing() => new()
    {
        Id = "studylife-cli",
        RedirectUriPattern = "http://127.0.0.1:8765/callback",
        RequestedScopes = new List<string> { "Notes.GetAll", "Notes.Create" },
    };

    private static OAuthClientInfoDto Registered(List<string> redirects, List<string> scopes) => new()
    {
        RedirectUris = redirects,
        RequestedScopes = scopes,
    };

    [Fact]
    public void TheListingsOwnRegistration_Matches_InAnyScopeOrder() =>
        Assert.True(MarketplaceRegistrationMatch.Matches(Listing(),
            Registered(["http://127.0.0.1:8765/callback"], ["Notes.Create", "Notes.GetAll"])));

    [Fact]
    public void AnExtraRedirectTarget_IsASquatter() =>
        Assert.False(MarketplaceRegistrationMatch.Matches(Listing(),
            Registered(["http://127.0.0.1:8765/callback", "https://attacker.test/cb"], ["Notes.GetAll", "Notes.Create"])));

    [Fact]
    public void AnotherRedirectTarget_IsASquatter() =>
        Assert.False(MarketplaceRegistrationMatch.Matches(Listing(),
            Registered(["https://attacker.test/cb"], ["Notes.GetAll", "Notes.Create"])));

    [Fact]
    public void WiderAccess_IsASquatter() =>
        Assert.False(MarketplaceRegistrationMatch.Matches(Listing(),
            Registered(["http://127.0.0.1:8765/callback"], ["Notes.GetAll", "Notes.Create", "Notes.Delete"])));

    [Fact]
    public void AListingWithoutRedirect_NeverMatches()
    {
        var listing = Listing();
        listing.RedirectUriPattern = "";
        Assert.False(MarketplaceRegistrationMatch.Matches(listing, Registered([], ["Notes.GetAll", "Notes.Create"])));
    }
}
