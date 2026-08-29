using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Generic OAuth-style connect flow (AuthController.10.OAuthClients.cs) - the dynamic-client
/// generalization of AuthController.5.Consent.cs's per-audience mechanism (mcp/capture/
/// focusguard/focustunes/tray, all still hardcoded and untouched). Seeds an OAuthClientEntity
/// directly via WithDbAsync (faster/more isolated than going through DeveloperController for
/// every test's setup) and drives the same two-step connect/assertion-exchange shape
/// TrayConnectFlowTests already pins for the hardcoded audiences - this file additionally covers
/// what's NEW here: per-client redirect URI allowlisting, dynamic scope enforcement, and the
/// granted-scopes snapshot (a developer adding a scope later must not silently widen access
/// already granted to an existing installer).
/// </summary>
public class GenericOAuthConnectFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GenericOAuthConnectFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string RedirectUri = "https://example-addon.test/callback";

    private static (string Assertion, string State) ParseRedirectTo(string redirectTo)
    {
        var uri = new Uri(redirectTo);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (query["assertion"] ?? "", query["state"] ?? "");
    }

    private async Task SeedClientAsync(string clientId, string redirectUri, params string[] scopes)
    {
        await _factory.WithDbAsync(async db =>
        {
            db.OAuthClients.Add(new OAuthClientEntity
            {
                ClientId = clientId,
                Name = "Example Add-on",
                Description = "A test add-on.",
                AllowedRedirectUris = redirectUri,
                RequestedScopes = string.Join(',', scopes),
                OwnerAuthUserId = 1,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
    }

    private Task CleanupAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        var client = await db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client != null) db.OAuthClients.Remove(client);
        var keys = await db.ClientApiKeys.Where(k => k.ClientId == clientId).ToListAsync();
        db.ClientApiKeys.RemoveRange(keys);
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task ConnectThenExchange_HappyPath_ReturnsRealUserIdAndAnApiKey()
    {
        await SeedClientAsync("happy-path-client", RedirectUri, "WebhooksProxy.List", "WebhooksProxy.Create");
        var sessionClient = _factory.CreateClient(); // seeded test user, AuthUserId 1

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "happy-path-client", RedirectUri = RedirectUri, State = "opaque-state-123" });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connectResult = await connectResponse.Content.ReadFromJsonAsync<GenericConnectResponseDto>();
        Assert.StartsWith(RedirectUri, connectResult!.RedirectTo);

        var (assertion, state) = ParseRedirectTo(connectResult.RedirectTo);
        Assert.Equal("opaque-state-123", state);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResponse = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "happy-path-client", Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>();
        Assert.Equal(1, exchangeResult!.UserId);
        Assert.False(string.IsNullOrEmpty(exchangeResult.ApiKey));

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult.ApiKey);
        var whoami = await keyClient.GetAsync("/api/auth/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var whoamiResult = await whoami.Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal("client:happy-path-client", whoamiResult!.Credential);

        // In scope: reaches the endpoint it was granted.
        var webhooksResponse = await keyClient.GetAsync("/api/webhooks");
        Assert.NotEqual(HttpStatusCode.Forbidden, webhooksResponse.StatusCode); // 503 (unconfigured), not 403

        await CleanupAsync("happy-path-client");
    }

    [Fact]
    public async Task OutOfScopeEndpoint_ReturnsForbidden()
    {
        await SeedClientAsync("narrow-client", RedirectUri, "WebhooksProxy.List");
        var sessionClient = _factory.CreateClient();

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "narrow-client", RedirectUri = RedirectUri, State = "s" });
        var (assertion, _) = ParseRedirectTo((await connectResponse.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!.RedirectTo);
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResult = await (await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "narrow-client", Assertion = assertion }))
            .Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>();

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult!.ApiKey);
        var response = await keyClient.GetAsync("/api/notes"); // never requested

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await CleanupAsync("narrow-client");
    }

    [Fact]
    public async Task Connect_WithRedirectUriNotOnTheClientsOwnAllowlist_ReturnsBadRequest()
    {
        await SeedClientAsync("strict-redirect-client", RedirectUri, "WebhooksProxy.List");
        var sessionClient = _factory.CreateClient();

        // A DIFFERENT, otherwise-valid https URL - not the one this client registered.
        var response = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "strict-redirect-client", RedirectUri = "https://not-registered.example.com/callback", State = "s" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await CleanupAsync("strict-redirect-client");
    }

    [Fact]
    public async Task Connect_WithUnknownClientId_ReturnsNotFound()
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "does-not-exist", RedirectUri = RedirectUri, State = "s" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssertionExchange_WithWrongClientId_ReturnsUnauthorized()
    {
        await SeedClientAsync("audience-a", RedirectUri, "WebhooksProxy.List");
        await SeedClientAsync("audience-b", RedirectUri, "WebhooksProxy.List");
        var sessionClient = _factory.CreateClient();

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "audience-a", RedirectUri = RedirectUri, State = "s" });
        var (assertion, _) = ParseRedirectTo((await connectResponse.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        // Presented at the WRONG client's identity - must not be redeemable there.
        var wrongAudience = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "audience-b", Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        // Still redeemable at the CORRECT client - a misdirected attempt must not burn it.
        var rightAudience = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "audience-a", Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, rightAudience.StatusCode);

        await CleanupAsync("audience-a");
        await CleanupAsync("audience-b");
    }

    [Fact]
    public async Task AssertionExchange_IsSingleUse_SecondAttemptIsRejected()
    {
        await SeedClientAsync("single-use-client", RedirectUri, "WebhooksProxy.List");
        var sessionClient = _factory.CreateClient();
        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "single-use-client", RedirectUri = RedirectUri, State = "s" });
        var (assertion, _) = ParseRedirectTo((await connectResponse.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!.RedirectTo);

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var first = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "single-use-client", Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "single-use-client", Assertion = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        await CleanupAsync("single-use-client");
    }

    /// <summary>The core security property ClientApiKeyEntity.GrantedScopes exists for: a
    /// developer adding a scope to their registration AFTER a key was already issued must not
    /// silently widen that already-issued key's access.</summary>
    [Fact]
    public async Task ScopeAddedAfterIssuance_DoesNotWidenAnAlreadyIssuedKey()
    {
        await SeedClientAsync("scope-snapshot-client", RedirectUri, "WebhooksProxy.List");
        var sessionClient = _factory.CreateClient();

        var connectResponse = await sessionClient.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = "scope-snapshot-client", RedirectUri = RedirectUri, State = "s" });
        var (assertion, _) = ParseRedirectTo((await connectResponse.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!.RedirectTo);
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchangeResult = await (await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = "scope-snapshot-client", Assertion = assertion }))
            .Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>();

        // Developer expands the registration's requested scopes AFTER the key was issued.
        await _factory.WithDbAsync(async db =>
        {
            var client = await db.OAuthClients.FirstAsync(c => c.ClientId == "scope-snapshot-client");
            client.RequestedScopes = "WebhooksProxy.List,Notes.GetAll";
            await db.SaveChangesAsync();
        });

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, exchangeResult!.ApiKey);
        var response = await keyClient.GetAsync("/api/notes"); // newly requested, but not in THIS key's snapshot

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await CleanupAsync("scope-snapshot-client");
    }

    [Fact]
    public async Task GetOAuthClientInfo_ReturnsNameDescriptionAndScopes()
    {
        await SeedClientAsync("info-client", RedirectUri, "WebhooksProxy.List", "Notes.GetAll");
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.GetAsync("/api/auth/oauth-clients/info-client");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<OAuthClientInfoDto>();
        Assert.Equal("Example Add-on", dto!.Name);
        Assert.Contains("WebhooksProxy.List", dto.RequestedScopes);
        Assert.Contains("Notes.GetAll", dto.RequestedScopes);

        await CleanupAsync("info-client");
    }

    [Fact]
    public async Task GetOAuthClientInfo_UnknownClientId_ReturnsNotFound()
    {
        var sessionClient = _factory.CreateClient();

        var response = await sessionClient.GetAsync("/api/auth/oauth-clients/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
