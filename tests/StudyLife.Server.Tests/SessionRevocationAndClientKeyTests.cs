using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Session revocation paths added by the 2026-09 audit (S7): "sign out everywhere else",
/// passkey removal killing the other sessions, and recovery login killing every prior
/// session. Sessions are issued straight through AuthSessionService (the same code the login
/// endpoints use) instead of a full WebAuthn ceremony.
/// </summary>
internal static class SessionTestHelpers
{
    public static Task<string> IssueSessionAsync(CustomWebApplicationFactory factory, int userId = 1) =>
        factory.WithDbAsync(async db =>
        {
            var token = AuthSessionService.IssueSession(db, userId, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return token;
        });

    public static HttpClient ClientWithSession(CustomWebApplicationFactory factory, string token)
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(factory, null);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);
        return client;
    }

    public static async Task<HttpStatusCode> ProbeAsync(HttpClient client) =>
        (await client.GetAsync("/api/auth/account-info")).StatusCode;
}

public class SessionRevocationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SessionRevocationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RevokeOthers_KillsEveryOtherSession_ButKeepsTheCaller()
    {
        var current = _factory.CreateClient(); // the factory's own seeded session
        using var otherA = SessionTestHelpers.ClientWithSession(_factory, await SessionTestHelpers.IssueSessionAsync(_factory));
        using var otherB = SessionTestHelpers.ClientWithSession(_factory, await SessionTestHelpers.IssueSessionAsync(_factory));
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(otherA));
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(otherB));

        var response = await current.PostAsync("/api/auth/sessions/revoke-others", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionTestHelpers.ProbeAsync(otherA));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionTestHelpers.ProbeAsync(otherB));
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(current));
    }

    [Fact]
    public async Task RevokeOthers_RequiresASession_NotAnApiKey()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.PostAsync("/api/auth/sessions/revoke-others", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCredential_RevokesOtherSessions_ButKeepsTheCaller()
    {
        // Two approved passkeys so that removing one is allowed (the last one is protected).
        var credentialIds = await _factory.WithDbAsync(async db =>
        {
            var a = new PasskeyCredentialEntity { AuthUserId = 1, CredentialId = RandomNumberGenerator.GetBytes(16), PublicKey = RandomNumberGenerator.GetBytes(32), CreatedAt = DateTime.UtcNow, ApprovedAt = DateTime.UtcNow };
            var b = new PasskeyCredentialEntity { AuthUserId = 1, CredentialId = RandomNumberGenerator.GetBytes(16), PublicKey = RandomNumberGenerator.GetBytes(32), CreatedAt = DateTime.UtcNow, ApprovedAt = DateTime.UtcNow };
            db.PasskeyCredentials.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });
        var current = _factory.CreateClient();
        using var other = SessionTestHelpers.ClientWithSession(_factory, await SessionTestHelpers.IssueSessionAsync(_factory));
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(other));

        var response = await current.DeleteAsync($"/api/auth/credentials/{credentialIds.Item1}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionTestHelpers.ProbeAsync(other));
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(current));

        await _factory.WithDbAsync(db => db.PasskeyCredentials.Where(c => c.Id == credentialIds.Item2).ExecuteDeleteAsync());
    }
}

/// <summary>Own factory: the recovery login below deliberately kills the factory's own seeded
/// session too, which would break sibling tests sharing that fixture.</summary>
public class RecoveryLoginRevocationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RecoveryLoginRevocationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RecoveryLogin_KillsEveryPriorSession_AndOnlyTheNewOneWorks()
    {
        var seeded = _factory.CreateClient();
        using var stale = SessionTestHelpers.ClientWithSession(_factory, await SessionTestHelpers.IssueSessionAsync(_factory));
        var codes = await (await seeded.PostAsync("/api/auth/recovery/generate", null)).Content.ReadFromJsonAsync<RecoveryCodesResponseDto>();

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var login = await anon.PostAsJsonAsync("/api/auth/recovery/login", new RecoveryLoginRequestDto { Code = codes!.Codes[0] });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var issued = await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();

        Assert.Equal(HttpStatusCode.Unauthorized, await SessionTestHelpers.ProbeAsync(stale));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionTestHelpers.ProbeAsync(seeded));
        using var fresh = SessionTestHelpers.ClientWithSession(_factory, issued!.Token);
        Assert.Equal(HttpStatusCode.OK, await SessionTestHelpers.ProbeAsync(fresh));
    }
}

/// <summary>
/// Issued add-on keys (ClientApiKeyEntity) are now listable and revocable by the user, and die
/// with their client registration (2026-09 audit S5) - before this a consent click minted a
/// permanent bearer key with no revocation path short of database surgery.
/// </summary>
public class ClientKeyManagementTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string RedirectUri = "https://example-addon.test/callback";

    public ClientKeyManagementTests(CustomWebApplicationFactory factory) => _factory = factory;

    private Task SeedClientAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            ClientId = clientId,
            Name = "Example Add-on",
            Description = "A test add-on.",
            AllowedRedirectUris = RedirectUri,
            RequestedScopes = "Courses.GetAll",
            OwnerAuthUserId = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });

    /// <summary>Runs the generic consent flow end to end and returns the plaintext key.</summary>
    private async Task<string> ConnectAsync(HttpClient session, string clientId)
    {
        var connect = await session.PostAsJsonAsync("/api/auth/connect",
            new GenericConnectRequestDto { ClientId = clientId, RedirectUri = RedirectUri, State = "s" });
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        var redirectTo = (await connect.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!.RedirectTo;
        var assertion = System.Web.HttpUtility.ParseQueryString(new Uri(redirectTo).Query)["assertion"]!;
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var exchange = await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = clientId, Assertion = assertion });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        return (await exchange.Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>())!.ApiKey;
    }

    [Fact]
    public async Task ListAndRevoke_KeyStopsAuthenticatingImmediately()
    {
        await SeedClientAsync("revocable-client");
        var session = _factory.CreateClient();
        var apiKey = await ConnectAsync(session, "revocable-client");
        using var addon = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        Assert.Equal(HttpStatusCode.OK, (await addon.GetAsync("/api/courses")).StatusCode);

        var list = await session.GetFromJsonAsync<List<ClientApiKeyListItemDto>>("/api/auth/client-keys");
        var row = Assert.Single(list!, k => k.ClientId == "revocable-client");
        Assert.Equal("Example Add-on", row.ClientName);
        Assert.Equal(["Courses.GetAll"], row.GrantedScopes);

        var revoke = await session.DeleteAsync($"/api/auth/client-keys/{row.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await addon.GetAsync("/api/courses")).StatusCode);
        Assert.DoesNotContain((await session.GetFromJsonAsync<List<ClientApiKeyListItemDto>>("/api/auth/client-keys"))!, k => k.Id == row.Id);
    }

    [Fact]
    public async Task Revoke_AnotherUsersKey_Is404_AndKeeps_TheRow()
    {
        var foreignId = await _factory.WithDbAsync(async db =>
        {
            var key = new ClientApiKeyEntity { AuthUserId = 2, ClientId = "foreign-client", GrantedScopes = "Courses.GetAll", KeyHash = AuthSessionService.HashToken(Guid.NewGuid().ToString()), CreatedAt = DateTime.UtcNow };
            db.ClientApiKeys.Add(key);
            await db.SaveChangesAsync();
            return key.Id;
        });
        var session = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await session.DeleteAsync($"/api/auth/client-keys/{foreignId}")).StatusCode);
        Assert.DoesNotContain((await session.GetFromJsonAsync<List<ClientApiKeyListItemDto>>("/api/auth/client-keys"))!, k => k.Id == foreignId);
        Assert.True(await _factory.WithDbAsync(db => db.ClientApiKeys.AnyAsync(k => k.Id == foreignId)));
    }

    [Fact]
    public async Task DeletingTheClientRegistration_DeletesItsIssuedKeys()
    {
        await SeedClientAsync("doomed-client");
        var session = _factory.CreateClient();
        var apiKey = await ConnectAsync(session, "doomed-client");
        using var addon = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        Assert.Equal(HttpStatusCode.OK, (await addon.GetAsync("/api/courses")).StatusCode);

        var delete = await session.DeleteAsync("/api/developer/clients/doomed-client");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await addon.GetAsync("/api/courses")).StatusCode);
        Assert.False(await _factory.WithDbAsync(db => db.ClientApiKeys.AnyAsync(k => k.ClientId == "doomed-client")));
    }

    [Fact]
    public async Task ListClientKeys_RequiresASession()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/client-keys")).StatusCode);
    }
}
