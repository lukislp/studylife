using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// CRUD for OAuthClientEntity via DeveloperController - the studylife-developers portal's only
/// backend dependency (see project_addon_marketplace_plan memory / the architecture artifact).
/// Own class/factory: creates and deletes OAuthClientEntity rows that would otherwise leak
/// between tests in the shared fixture.
/// </summary>
public class DeveloperControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DeveloperControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // seeded test user, AuthUserId 1
    }

    private static CreateDeveloperClientRequestDto ValidRequest(string clientId = "zapier-integration") => new()
    {
        ClientId = clientId,
        Name = "Zapier Integration",
        Description = "Fires a Zap on every completed session.",
        AllowedRedirectUris = new List<string> { "https://hooks.zapier.com/callback" },
        RequestedScopes = new List<string> { "WebhooksProxy.List", "WebhooksProxy.Create" },
    };

    private async Task<HttpClient> CreateClientForNewUserAsync(string displayName)
    {
        var token = await _factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = displayName, CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            var issuedToken = AuthSessionService.IssueSession(db, user.Id, DateTime.UtcNow);
            await db.SaveChangesAsync(); // IssueSession itself doesn't save - see its own doc comment
            return issuedToken;
        });
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);
        return client;
    }

    [Fact]
    public async Task CreateThenGetAll_RoundTrips()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/developer/clients", ValidRequest());
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.NotNull(created);
        Assert.Equal("zapier-integration", created!.ClientId);
        Assert.Equal(new List<string> { "WebhooksProxy.List", "WebhooksProxy.Create" }, created.RequestedScopes);

        var list = await _client.GetFromJsonAsync<List<DeveloperClientDto>>("/api/developer/clients");
        Assert.Contains(list!, c => c.ClientId == "zapier-integration");

        await _client.DeleteAsync("/api/developer/clients/zapier-integration");
    }

    [Fact]
    public async Task Create_DuplicateClientId_ReturnsBadRequest()
    {
        await _client.PostAsJsonAsync("/api/developer/clients", ValidRequest("dup-client"));

        var second = await _client.PostAsJsonAsync("/api/developer/clients", ValidRequest("dup-client"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        await _client.DeleteAsync("/api/developer/clients/dup-client");
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPERCASE")]
    [InlineData("under_score")]
    [InlineData("")]
    public async Task Create_InvalidClientIdFormat_ReturnsBadRequest(string clientId)
    {
        var request = ValidRequest(clientId);
        var response = await _client.PostAsJsonAsync("/api/developer/clients", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_RedirectUriNotHttpsOrLoopback_ReturnsBadRequest()
    {
        var request = ValidRequest("bad-redirect-client");
        request.AllowedRedirectUris = new List<string> { "http://example.com/callback" };

        var response = await _client.PostAsJsonAsync("/api/developer/clients", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ScopeNotInPubliclyGrantableList_ReturnsBadRequest()
    {
        var request = ValidRequest("over-scoped-client");
        request.RequestedScopes = new List<string> { "Settings.Save" }; // deliberately excluded, see ApiKeyScopes.PubliclyGrantable

        var response = await _client.PostAsJsonAsync("/api/developer/clients", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_AddsAScope_PersistsOnTheRegistrationOnly()
    {
        await _client.PostAsJsonAsync("/api/developer/clients", ValidRequest("expandable-client"));

        var update = new UpdateDeveloperClientRequestDto
        {
            Name = "Zapier Integration",
            Description = "Now also reads notes.",
            AllowedRedirectUris = new List<string> { "https://hooks.zapier.com/callback" },
            RequestedScopes = new List<string> { "WebhooksProxy.List", "WebhooksProxy.Create", "Notes.GetAll" },
        };
        var response = await _client.PutAsJsonAsync("/api/developer/clients/expandable-client", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<DeveloperClientDto>();
        Assert.Contains("Notes.GetAll", updated!.RequestedScopes);

        await _client.DeleteAsync("/api/developer/clients/expandable-client");
    }

    [Fact]
    public async Task AnotherUsersClient_CannotBeUpdatedOrDeleted_ReturnsNotFound()
    {
        await _client.PostAsJsonAsync("/api/developer/clients", ValidRequest("owned-by-user-one"));

        using var otherUserClient = await CreateClientForNewUserAsync("Someone Else");
        var updateResponse = await otherUserClient.PutAsJsonAsync("/api/developer/clients/owned-by-user-one",
            new UpdateDeveloperClientRequestDto { Name = "Hijacked", AllowedRedirectUris = new List<string> { "https://evil.example.com" }, RequestedScopes = new List<string> { "Notes.GetAll" } });
        Assert.Equal(HttpStatusCode.NotFound, updateResponse.StatusCode);

        var deleteResponse = await otherUserClient.DeleteAsync("/api/developer/clients/owned-by-user-one");
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);

        // Untouched: still exists and still owned by user 1, listed there.
        var list = await _client.GetFromJsonAsync<List<DeveloperClientDto>>("/api/developer/clients");
        Assert.Contains(list!, c => c.ClientId == "owned-by-user-one" && c.Name == "Zapier Integration");

        // A different owner's list must never include another user's client either.
        var otherList = await otherUserClient.GetFromJsonAsync<List<DeveloperClientDto>>("/api/developer/clients");
        Assert.DoesNotContain(otherList!, c => c.ClientId == "owned-by-user-one");

        await _client.DeleteAsync("/api/developer/clients/owned-by-user-one");
    }

    [Fact]
    public async Task GetAll_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.GetAsync("/api/developer/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
