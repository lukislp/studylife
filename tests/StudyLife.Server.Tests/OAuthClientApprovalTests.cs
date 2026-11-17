// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>The pure review rules (2026-11-16 audit M5).</summary>
public class OAuthClientApprovalPolicyTests
{
    [Fact]
    public void NewRegistration_IsApprovedOnlyForTheOwner()
    {
        Assert.Equal(OAuthClientApproval.Approved, OAuthClientApprovalPolicy.ForNewRegistration(registrantIsOwner: true));
        Assert.Equal(OAuthClientApproval.Pending, OAuthClientApprovalPolicy.ForNewRegistration(registrantIsOwner: false));
    }

    [Theory]
    [InlineData(OAuthClientApproval.Approved, true, false, OAuthClientApproval.Pending)]
    [InlineData(OAuthClientApproval.Rejected, true, false, OAuthClientApproval.Pending)]
    [InlineData(OAuthClientApproval.Approved, false, false, OAuthClientApproval.Approved)]
    [InlineData(OAuthClientApproval.Rejected, false, false, OAuthClientApproval.Rejected)]
    [InlineData(OAuthClientApproval.Pending, true, true, OAuthClientApproval.Approved)]
    [InlineData(OAuthClientApproval.Approved, true, true, OAuthClientApproval.Approved)]
    public void AfterEdit_ANonOwnersChangeGoesBackToReview(OAuthClientApproval current, bool changed, bool editorIsOwner, OAuthClientApproval expected) =>
        Assert.Equal(expected, OAuthClientApprovalPolicy.AfterEdit(current, changed, editorIsOwner));

    [Fact]
    public void WireValues_AreTheSharedConstants()
    {
        Assert.Equal(OAuthClientApprovalStates.Pending, OAuthClientApprovalPolicy.ToWire(OAuthClientApproval.Pending));
        Assert.Equal(OAuthClientApprovalStates.Approved, OAuthClientApprovalPolicy.ToWire(OAuthClientApproval.Approved));
        Assert.Equal(OAuthClientApprovalStates.Rejected, OAuthClientApprovalPolicy.ToWire(OAuthClientApproval.Rejected));
    }
}

/// <summary>
/// Instance-wide client registration under owner review, end to end (2026-11-16 audit M5): a
/// registration by anyone but the owner cannot reach the consent step until the owner approves
/// it, the consent lookup names the registrant, and a registrant's later edit sends the client
/// back to review. Own fixture: the rows created here must not leak into other classes.
/// </summary>
public class OAuthClientApprovalTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string RedirectUri = "https://approval-addon.test/callback";
    // RFC 7636 appendix B pair: the connect calls below send PKCE so they hold both with and
    // without a server that makes PKCE mandatory for dynamic clients.
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private readonly CustomWebApplicationFactory _factory;

    public OAuthClientApprovalTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CreateDeveloperClientRequestDto Registration(string clientId, string redirectUri = RedirectUri) => new()
    {
        ClientId = clientId,
        Name = "Approval Add-on",
        Description = "Reads webhooks.",
        AllowedRedirectUris = new List<string> { redirectUri },
        RequestedScopes = new List<string> { "WebhooksProxy.List" },
    };

    private static GenericConnectRequestDto ConnectRequest(string clientId) => new()
    {
        ClientId = clientId,
        RedirectUri = RedirectUri,
        State = "s",
        Scopes = new List<string> { "WebhooksProxy.List" },
        CodeChallenge = Challenge,
        CodeChallengeMethod = "S256",
    };

    private async Task<HttpClient> NonOwnerClientAsync(string displayName)
    {
        var token = await _factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = displayName, CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            var issued = AuthSessionService.IssueSession(db, user.Id, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return issued;
        });
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);
        return client;
    }

    private Task CleanupAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        await db.ClientApiKeys.Where(k => k.ClientId == clientId).ExecuteDeleteAsync();
        await db.OAuthClients.Where(c => c.ClientId == clientId).ExecuteDeleteAsync();
    });

    [Fact]
    public async Task OwnerRegistration_IsApprovedDirectly_AndNamesTheOwnerOnConsent()
    {
        var owner = _factory.CreateClient(); // seeded user 1, the instance owner
        var created = await (await owner.PostAsJsonAsync("/api/developer/clients", Registration("owner-addon")))
            .Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.Equal(OAuthClientApprovalStates.Approved, created!.Approval);

        var info = await owner.GetFromJsonAsync<OAuthClientInfoDto>("/api/auth/oauth-clients/owner-addon");
        Assert.True(info!.RegisteredByOwner);
        Assert.False(string.IsNullOrEmpty(info.RegisteredBy));
        Assert.Equal(new List<string> { RedirectUri }, info.RedirectUris);

        var connect = await owner.PostAsJsonAsync("/api/auth/connect", ConnectRequest("owner-addon"));
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        await CleanupAsync("owner-addon");
    }

    [Fact]
    public async Task NonOwnerRegistration_WaitsForTheOwner_ThenConnects()
    {
        using var developer = await NonOwnerClientAsync("Mallory");
        var created = await (await developer.PostAsJsonAsync("/api/developer/clients", Registration("pending-addon")))
            .Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.Equal(OAuthClientApprovalStates.Pending, created!.Approval);

        // Every user's consent screen sees who registered it and that it is still waiting.
        var victim = _factory.CreateClient();
        var info = await victim.GetFromJsonAsync<OAuthClientInfoDto>("/api/auth/oauth-clients/pending-addon");
        Assert.Equal("Mallory", info!.RegisteredBy);
        Assert.False(info.RegisteredByOwner);
        Assert.Equal(OAuthClientApprovalStates.Pending, info.Approval);

        var refused = await victim.PostAsJsonAsync("/api/auth/connect", ConnectRequest("pending-addon"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("client-not-approved", await refused.Content.ReadAsStringAsync());
        Assert.False(await _factory.WithDbAsync(db => db.ClientApiKeys.AnyAsync(k => k.ClientId == "pending-addon")));

        var review = await victim.GetFromJsonAsync<List<OAuthClientReviewDto>>("/api/oauth-client-reviews");
        var row = Assert.Single(review!, r => r.ClientId == "pending-addon");
        Assert.Equal("Mallory", row.RegisteredBy);
        Assert.Equal(new List<string> { RedirectUri }, row.RedirectUris);

        var approved = await victim.PostAsync("/api/oauth-client-reviews/pending-addon/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(OAuthClientApprovalStates.Approved, (await approved.Content.ReadFromJsonAsync<OAuthClientReviewDto>())!.Approval);

        var connect = await victim.PostAsJsonAsync("/api/auth/connect", ConnectRequest("pending-addon"));
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        await CleanupAsync("pending-addon");
    }

    [Fact]
    public async Task RegistrantEdit_OfAnApprovedClient_GoesBackToReview_ANoOpSaveDoesNot()
    {
        using var developer = await NonOwnerClientAsync("Trent");
        await developer.PostAsJsonAsync("/api/developer/clients", Registration("edited-addon"));
        await _factory.CreateClient().PostAsync("/api/oauth-client-reviews/edited-addon/approve", null);

        var unchanged = Registration("edited-addon");
        var noOp = await (await developer.PutAsJsonAsync("/api/developer/clients/edited-addon", new UpdateDeveloperClientRequestDto
        {
            Name = unchanged.Name,
            Description = unchanged.Description,
            AllowedRedirectUris = unchanged.AllowedRedirectUris,
            RequestedScopes = unchanged.RequestedScopes,
        })).Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.Equal(OAuthClientApprovalStates.Approved, noOp!.Approval);

        // Swapping the redirect target after approval is exactly the attack the review prevents.
        var swapped = await (await developer.PutAsJsonAsync("/api/developer/clients/edited-addon", new UpdateDeveloperClientRequestDto
        {
            Name = unchanged.Name,
            Description = unchanged.Description,
            AllowedRedirectUris = new List<string> { "https://attacker.test/cb" },
            RequestedScopes = unchanged.RequestedScopes,
        })).Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.Equal(OAuthClientApprovalStates.Pending, swapped!.Approval);

        await CleanupAsync("edited-addon");
    }

    [Fact]
    public async Task Reject_RevokesKeysAlreadyIssued()
    {
        var owner = _factory.CreateClient();
        await owner.PostAsJsonAsync("/api/developer/clients", Registration("rejected-addon"));
        await owner.PostAsJsonAsync("/api/auth/connect", ConnectRequest("rejected-addon"));
        Assert.True(await _factory.WithDbAsync(db => db.ClientApiKeys.AnyAsync(k => k.ClientId == "rejected-addon")));

        var rejected = await owner.PostAsync("/api/oauth-client-reviews/rejected-addon/reject", null);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.False(await _factory.WithDbAsync(db => db.ClientApiKeys.AnyAsync(k => k.ClientId == "rejected-addon")));
        var connect = await owner.PostAsJsonAsync("/api/auth/connect", ConnectRequest("rejected-addon"));
        Assert.Equal(HttpStatusCode.Forbidden, connect.StatusCode);

        await CleanupAsync("rejected-addon");
    }

    [Fact]
    public async Task ReviewEndpoints_AreOwnerOnly()
    {
        using var developer = await NonOwnerClientAsync("Eve");
        await developer.PostAsJsonAsync("/api/developer/clients", Registration("self-approve-addon"));

        Assert.Equal(HttpStatusCode.Forbidden, (await developer.GetAsync("/api/oauth-client-reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsync("/api/oauth-client-reviews/self-approve-addon/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsync("/api/oauth-client-reviews/self-approve-addon/reject", null)).StatusCode);
        var state = await _factory.WithDbAsync(db => db.OAuthClients.Where(c => c.ClientId == "self-approve-addon").Select(c => c.Approval).SingleAsync());
        Assert.Equal(OAuthClientApproval.Pending, state);

        await CleanupAsync("self-approve-addon");
    }

    [Fact]
    public async Task Approve_UnknownClient_IsNotFound()
    {
        var response = await _factory.CreateClient().PostAsync("/api/oauth-client-reviews/no-such-addon/approve", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
