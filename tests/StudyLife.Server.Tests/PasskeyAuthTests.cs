using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>Shared HTTP helpers for the passkey test classes. Origin/RP ID result from the
/// WebApplicationFactory base address (http://localhost) - the AuthController derives its
/// Fido2 config per request from exactly that.</summary>
internal static class PasskeyHttp
{
    public const string Origin = "http://localhost";

    public static async Task<(string OptionsId, string OptionsJson)> BeginAsync(
        HttpClient client, string path, object? body = null, string? sessionToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (sessionToken is not null) request.Headers.Add("X-Session-Token", sessionToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(dto);
        return (dto!.OptionsId, dto.OptionsJson);
    }

    public static async Task<HttpResponseMessage> CompleteAsync(
        HttpClient client, string path, string optionsId, JsonNode assertionOrAttestation, string? sessionToken = null)
    {
        var body = new JsonObject { ["optionsId"] = optionsId, ["response"] = assertionOrAttestation };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (sessionToken is not null) request.Headers.Add("X-Session-Token", sessionToken);
        return await client.SendAsync(request);
    }

    /// <summary>Complete registration flow (begin → authenticator → complete), returns the
    /// issued session token. Resolves the currently valid setup code from the factory
    /// and always sends it along - harmless if it's not needed at all (the server
    /// only checks it on the very first registration, see AuthController.RegisterBegin).</summary>
    public static async Task<string> RegisterAsync(
        CustomWebApplicationFactory factory, HttpClient client, string displayName, FakePasskey passkey)
    {
        using var scope = factory.Services.CreateScope();
        var setupSecret = await scope.ServiceProvider.GetRequiredService<SystemSecretsService>().EnsureSetupSecretAsync();
        var (optionsId, optionsJson) = await BeginAsync(client, "/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = displayName, SetupSecret = setupSecret });
        var response = await CompleteAsync(client, "/api/auth/register/complete", optionsId,
            passkey.CreateAttestationResponse(optionsJson, Origin));
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"register/complete: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
        var dto = await response.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(dto?.Token);
        return dto!.Token!;
    }

    public static async Task<HttpResponseMessage> GetWithTokenAsync(HttpClient client, string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Session-Token", token);
        return await client.SendAsync(request);
    }
}

/// <summary>
/// Registration case distinction (phase 2): the FIRST registration ever claims
/// the legacy user created by the phase 1 migration along with its existing data, every
/// further one creates a brand-new, empty user. A scenario test instead of multiple [Fact]s,
/// because the steps causally build on each other (xUnit guarantees no order within a
/// class, and the cases are defined precisely BY the history).
/// </summary>
public class PasskeyRegistrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasskeyRegistrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Registration_ClaimsLegacyUserFirst_ThenCreatesFreshUsers()
    {
        // ── Step 1: first registration claims the legacy user ────────────────────────
        // Create the legacy user's existing data (as it exists after the phase 1 migration).
        await _factory.WithDbAsync(async db =>
        {
            var legacy = await db.AuthUsers.SingleAsync();
            db.Notes.Add(new NoteEntity
            {
                AuthUserId = legacy.Id,
                Title = "Alt-Notiz",
                Content = "Inhalt",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        using var alexKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", alexKey);

        var legacyUserId = await _factory.WithDbAsync(async db =>
        {
            // NO second user row - the existing user was renamed and claimed.
            var user = await db.AuthUsers.SingleAsync();
            Assert.Equal("Alex", user.DisplayName);

            var credential = await db.PasskeyCredentials.SingleAsync();
            Assert.Equal(user.Id, credential.AuthUserId);
            Assert.Equal(alexKey.CredentialId, credential.CredentialId);
            return user.Id;
        });

        // The existing data still belongs to the registering person: visible with their new
        // session (CORRECT AuthUserId from the session, not the phase 1 fallback).
        var alexNotes = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", alexToken);
        Assert.Equal(HttpStatusCode.OK, alexNotes.StatusCode);
        Assert.Contains("Alt-Notiz", await alexNotes.Content.ReadAsStringAsync());

        // ── Step 2: second registration creates a NEW, empty user ──────────────
        using var annaKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", annaKey);

        var annaUserId = await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.AuthUsers.CountAsync());
            var anna = await db.AuthUsers.SingleAsync(u => u.DisplayName == "Anna");
            Assert.NotEqual(legacyUserId, anna.Id);
            var annaCredential = await db.PasskeyCredentials.SingleAsync(c => c.AuthUserId == anna.Id);
            Assert.Equal(annaKey.CredentialId, annaCredential.CredentialId);
            // Completely empty record: no inherited notes/sessions/settings.
            Assert.False(await db.Notes.IgnoreQueryFilters().AnyAsync(n => n.AuthUserId == anna.Id));
            Assert.False(await db.Sessions.IgnoreQueryFilters().AnyAsync(s => s.AuthUserId == anna.Id));
            return anna.Id;
        });

        // ── Step 3: data isolation between two logged-in users (regression against phase 1) ─
        var annaNotes = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", annaToken);
        Assert.Equal(HttpStatusCode.OK, annaNotes.StatusCode);
        Assert.DoesNotContain("Alt-Notiz", await annaNotes.Content.ReadAsStringAsync());

        // ── Step 4: additional passkey for the same account (session required) ─────────────
        // Without a session: 401. _client carries its own valid default session token via
        // ConfigureClient - hence a truly anonymous client for this.
        using (var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null))
        {
            var denied = await anon.PostAsync("/api/auth/register/begin-additional", null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // With Anna's session: excludeCredentials contains her existing passkey, the new one
        // attaches to the SAME user, no additional user and NO new session is created.
        var (additionalId, additionalJson) = await PasskeyHttp.BeginAsync(
            _client, "/api/auth/register/begin-additional", body: null, sessionToken: annaToken);
        using (var optionsDoc = JsonDocument.Parse(additionalJson))
        {
            var excluded = optionsDoc.RootElement.GetProperty("excludeCredentials")
                .EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
            Assert.Contains(annaKey.CredentialIdBase64Url, excluded);
        }

        using var annaSecondKey = new FakePasskey();
        var additionalComplete = await PasskeyHttp.CompleteAsync(_client, "/api/auth/register/complete",
            additionalId, annaSecondKey.CreateAttestationResponse(additionalJson, PasskeyHttp.Origin),
            sessionToken: annaToken);
        Assert.Equal(HttpStatusCode.OK, additionalComplete.StatusCode);
        var additionalDto = await additionalComplete.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.Null(additionalDto!.Token);

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.AuthUsers.CountAsync());
            Assert.Equal(2, await db.PasskeyCredentials.CountAsync(c => c.AuthUserId == annaUserId));
        });
    }
}

/// <summary>
/// Login verification via the real Fido2NetLib path: correct signature is accepted,
/// foreign keys and sign-count regressions (replay/cloned authenticator) are rejected
/// with 401. Own factory (fresh DB) per class.
/// </summary>
public class PasskeyLoginTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasskeyLoginTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_VerifiesSignature_AndRejectsReplayAndForeignKeys()
    {
        using var passkey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", passkey);

        // ── Correct signature with increased sign count: accepted ─────────────────────────
        // allowCredentials deliberately stays EMPTY (ResidentKey=Required, discoverable credentials) -
        // a populated array would reveal all users' credential IDs to any anonymous caller.
        var (loginId, loginJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        using (var optionsDoc = JsonDocument.Parse(loginJson))
        {
            Assert.Empty(optionsDoc.RootElement.GetProperty("allowCredentials").EnumerateArray());
        }

        var ok = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", loginId,
            passkey.CreateAssertionResponse(loginJson, PasskeyHttp.Origin, signCount: 5));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okDto = await ok.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(okDto?.Token);
        Assert.Equal("Alex", okDto!.DisplayName);

        await _factory.WithDbAsync(async db =>
        {
            var credential = await db.PasskeyCredentials.SingleAsync();
            Assert.Equal(5u, credential.SignCount);
            Assert.NotNull(credential.LastUsedAt);
        });

        // ── Foreign key (same credential ID, wrong signature): 401 ─────────────────
        using var foreignKey = new FakePasskey();
        var (foreignId, foreignJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var forged = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", foreignId,
            passkey.CreateAssertionResponse(foreignJson, PasskeyHttp.Origin, signCount: 6, signWith: foreignKey));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // ── Sign-count regression despite correct signature (replay/cloned key): 401 ──────
        var (replayId, replayJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var replay = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", replayId,
            passkey.CreateAssertionResponse(replayJson, PasskeyHttp.Origin, signCount: 5));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ── Unknown credential ID: 401 ─────────────────────────────────────────────────────
        using var unknownKey = new FakePasskey();
        var (unknownId, unknownJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var unknown = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", unknownId,
            unknownKey.CreateAssertionResponse(unknownJson, PasskeyHttp.Origin, signCount: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        // Sign count unchanged at 5 - none of the rejected attempts must have touched it.
        await _factory.WithDbAsync(async db =>
            Assert.Equal(5u, (await db.PasskeyCredentials.SingleAsync()).SignCount));
    }

    [Fact]
    public async Task LoginBegin_WithoutAnyCredential_ReturnsBadRequest()
    {
        // Runs against the same class DB as the scenario test - hence a SEPARATE factory,
        // whose DB is guaranteed to be empty of credentials.
        using var emptyFactory = new CustomWebApplicationFactory();
        var client = emptyFactory.CreateClient();
        var response = await client.PostAsync("/api/auth/login/begin", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// Sliding session semantics of the gate/resolution middleware: extension to now+90 days
/// on every valid request, hard ceiling HardExpiresAt, 401 for expired sessions
/// even ALONGSIDE a valid API key, logout invalidates server-side.
/// </summary>
public class PasskeySessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasskeySessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> InsertSessionAsync(DateTime issuedAt, DateTime expiresAt, DateTime hardExpiresAt)
    {
        var token = AuthSessionService.GenerateToken();
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.OrderBy(u => u.Id).FirstAsync();
            db.AuthSessions.Add(new AuthSessionEntity
            {
                AuthUserId = user.Id,
                TokenHash = AuthSessionService.HashToken(token),
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                HardExpiresAt = hardExpiresAt,
                LastUsedAt = issuedAt,
            });
            await db.SaveChangesAsync();
        });
        return token;
    }

    private async Task<AuthSessionEntity> LoadSessionAsync(string token) =>
        await _factory.WithDbAsync(async db =>
            await db.AuthSessions.AsNoTracking()
                .SingleAsync(s => s.TokenHash == AuthSessionService.HashToken(token)));

    [Fact]
    public async Task ValidRequest_SlidesExpiresAtForward()
    {
        var now = DateTime.UtcNow;
        var token = await InsertSessionAsync(now.AddDays(-1), now.AddDays(1), now.AddDays(179));

        var response = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await LoadSessionAsync(token);
        Assert.True(session.ExpiresAt > now.AddDays(89), $"ExpiresAt {session.ExpiresAt:O} was not extended on a sliding basis");
        Assert.True(session.LastUsedAt >= now.AddMinutes(-1));
    }

    [Fact]
    public async Task SlidingRefresh_NeverExceedsHardExpiresAt()
    {
        var now = DateTime.UtcNow;
        var hard = now.AddDays(20);
        var token = await InsertSessionAsync(now.AddDays(-160), now.AddDays(10), hard);

        // Multiple requests: ExpiresAt stays capped exactly at HardExpiresAt, no matter how often.
        for (var i = 0; i < 3; i++)
        {
            var response = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var session = await LoadSessionAsync(token);
        Assert.Equal(hard, session.ExpiresAt, TimeSpan.FromSeconds(1));
        Assert.Equal(hard, session.HardExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ExpiredSession_Returns401_EvenWithValidApiKey()
    {
        var now = DateTime.UtcNow;
        var token = await InsertSessionAsync(now.AddDays(-100), now.AddMinutes(-5), now.AddDays(80));

        // The test client sends the valid API key along as a default header - still 401:
        // a session token that is sent but invalid must NEVER silently fall back to the
        // API-key fallback user (that's the signal the client uses to discard the
        // token and redirect to login).
        var response = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Control: the same request WITHOUT a token continues via the API-key path as before.
        var withoutToken = await _client.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.OK, withoutToken.StatusCode);
    }

    [Fact]
    public async Task HardExpiredSession_Returns401_EvenIfSlidingWindowStillOpen()
    {
        var now = DateTime.UtcNow;
        // Inconsistent edge case (should never arise due to the capping, but is the
        // security-critical direction): ExpiresAt in the future, HardExpiresAt exceeded.
        var token = await InsertSessionAsync(now.AddDays(-181), now.AddDays(1), now.AddMinutes(-1));

        var response = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionToken_AloneWithoutApiKey_PassesTheGate()
    {
        var now = DateTime.UtcNow;
        var token = await InsertSessionAsync(now, now.AddDays(90), now.AddDays(180));

        // Own client WITHOUT the factory's default headers (neither X-Api-Key nor the default
        // session token logged in via ConfigureClient): the supplied session token is
        // an EQUIVALENT way through the gate.
        var bareClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await PasskeyHttp.GetWithTokenAsync(bareClient, "/api/notes", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Without either, the gate stays closed.
        var denied = await bareClient.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Logout_InvalidatesTheSessionServerSide()
    {
        var now = DateTime.UtcNow;
        var token = await InsertSessionAsync(now, now.AddDays(90), now.AddDays(180));

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-Session-Token", token);
        var logout = await _client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        // Logout without a (valid) session: 401 instead of silent success. _client carries its
        // own valid default session token via ConfigureClient - hence test anonymously for this.
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var unauthenticated = await anon.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }
}

/// <summary>
/// Passkey management (GET/PUT/DELETE /api/auth/credentials): session required, only own
/// passkeys visible, last passkey not deletable.
/// </summary>
public class PasskeyCredentialManagementTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasskeyCredentialManagementTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CredentialManagement_ListsRenamesAndGuardsLastPasskey()
    {
        // Without a session (API key only): 401 - management is gated on the REAL session. _client
        // carries its own valid default session token via ConfigureClient - hence anonymous here.
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var denied = await anon.GetAsync("/api/auth/credentials");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var passkey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", passkey);

        var list = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/credentials", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = await list.Content.ReadFromJsonAsync<List<PasskeyListItemDto>>();
        var item = Assert.Single(items!);
        Assert.Null(item.DeviceLabel);

        // Rename (DeviceLabel freely editable).
        using var renameRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/auth/credentials/{item.Id}/label")
        {
            Content = JsonContent.Create(new PasskeyRenameRequestDto { Label = "Alex's iPhone" }),
        };
        renameRequest.Headers.Add("X-Session-Token", token);
        var rename = await _client.SendAsync(renameRequest);
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        await _factory.WithDbAsync(async db =>
            Assert.Equal("Alex's iPhone", (await db.PasskeyCredentials.SingleAsync()).DeviceLabel));

        // The last passkey of an account is not deletable (self-lockout protection).
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/credentials/{item.Id}");
        deleteRequest.Headers.Add("X-Session-Token", token);
        var blocked = await _client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        await _factory.WithDbAsync(async db => Assert.Equal(1, await db.PasskeyCredentials.CountAsync()));
    }
}

/// <summary>
/// Approval workflow for additional passkeys: a passkey created via register/begin-additional
/// stays PENDING and CANNOT log in until an already logged-in device of the same
/// account consents via /credentials/{id}/approve (security requirement so a stolen/
/// reused session token alone can't create a permanent second means of access).
/// </summary>
public class PasskeyApprovalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasskeyApprovalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AdditionalPasskey_StaysPendingUntilApproved_ThenCanLogin()
    {
        using var firstKey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);

        // ── Register an additional passkey: stays PENDING, no session of its own ───────────────
        var (beginId, beginJson) = await PasskeyHttp.BeginAsync(
            _client, "/api/auth/register/begin-additional", body: null, sessionToken: token);
        using var secondKey = new FakePasskey();
        var complete = await PasskeyHttp.CompleteAsync(_client, "/api/auth/register/complete",
            beginId, secondKey.CreateAttestationResponse(beginJson, PasskeyHttp.Origin), sessionToken: token);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completeDto = await complete.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.True(completeDto!.Pending);
        Assert.Null(completeDto.Token);

        var credentialId = await _factory.WithDbAsync(async db =>
        {
            var cred = await db.PasskeyCredentials.SingleAsync(c => c.CredentialId == secondKey.CredentialId);
            Assert.Null(cred.ApprovedAt);
            return cred.Id;
        });

        // ── Device list shows exactly one pending and one approved passkey ──────────
        var list = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/credentials", token);
        var items = await list.Content.ReadFromJsonAsync<List<PasskeyListItemDto>>();
        Assert.Equal(2, items!.Count);
        Assert.Single(items, i => i.Pending);
        Assert.Single(items, i => !i.Pending);

        // ── Login attempt with the NOT-approved passkey: 401 + "pending_approval" ─────
        var (loginId, loginJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var deniedLogin = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", loginId,
            secondKey.CreateAssertionResponse(loginJson, PasskeyHttp.Origin, signCount: 2));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);
        using (var deniedDoc = JsonDocument.Parse(await deniedLogin.Content.ReadAsStringAsync()))
            Assert.Equal("pending_approval", deniedDoc.RootElement.GetProperty("error").GetString());

        // The rejected attempt (despite a valid signature) must NOT have moved the sign count -
        // otherwise a second, real login attempt later could falsely count as a replay.
        await _factory.WithDbAsync(async db =>
            Assert.Equal(1u, (await db.PasskeyCredentials.SingleAsync(c => c.Id == credentialId)).SignCount));

        // ── Approval via a FOREIGN account: 404 (not its own credential) ──────────
        using var otherKey = new FakePasskey();
        var otherToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", otherKey);
        using (var foreignApprove = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/credentials/{credentialId}/approve"))
        {
            foreignApprove.Headers.Add("X-Session-Token", otherToken);
            var foreignResponse = await _client.SendAsync(foreignApprove);
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        }

        // ── Approval without a session: 401 ───────────────────────────────────────────────────────
        using (var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null))
        {
            var anonApprove = await anon.PostAsync($"/api/auth/credentials/{credentialId}/approve", null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonApprove.StatusCode);
        }

        // ── Approval by the OWN (first) device: 204, idempotent afterward ────────────────────
        using (var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/credentials/{credentialId}/approve"))
        {
            approve.Headers.Add("X-Session-Token", token);
            var approveResponse = await _client.SendAsync(approve);
            Assert.Equal(HttpStatusCode.NoContent, approveResponse.StatusCode);
        }
        using (var approveAgain = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/credentials/{credentialId}/approve"))
        {
            approveAgain.Headers.Add("X-Session-Token", token);
            var approveAgainResponse = await _client.SendAsync(approveAgain);
            Assert.Equal(HttpStatusCode.NoContent, approveAgainResponse.StatusCode);
        }

        // ── Login with the now-approved passkey: successful ────────────────────────────
        var (loginId2, loginJson2) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var okLogin = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete", loginId2,
            secondKey.CreateAssertionResponse(loginJson2, PasskeyHttp.Origin, signCount: 3));
        Assert.Equal(HttpStatusCode.OK, okLogin.StatusCode);
        var okDto = await okLogin.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(okDto?.Token);
        Assert.Equal("Alex", okDto!.DisplayName);
    }

    [Fact]
    public async Task DeleteCredential_IgnoresPendingCredentials_WhenGuardingLastPasskey()
    {
        using var firstKey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);

        var (beginId, beginJson) = await PasskeyHttp.BeginAsync(
            _client, "/api/auth/register/begin-additional", body: null, sessionToken: token);
        using var secondKey = new FakePasskey();
        await PasskeyHttp.CompleteAsync(_client, "/api/auth/register/complete",
            beginId, secondKey.CreateAttestationResponse(beginJson, PasskeyHttp.Origin), sessionToken: token);

        var (approvedId, pendingId) = await _factory.WithDbAsync(async db =>
        {
            var approved = await db.PasskeyCredentials.SingleAsync(c => c.CredentialId == firstKey.CredentialId);
            var pending = await db.PasskeyCredentials.SingleAsync(c => c.CredentialId == secondKey.CredentialId);
            return (approved.Id, pending.Id);
        });

        // The not-yet-approved passkey does NOT count as "last" - deleting the only
        // approved passkey remains blocked nonetheless.
        using (var deleteApproved = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/credentials/{approvedId}"))
        {
            deleteApproved.Headers.Add("X-Session-Token", token);
            var response = await _client.SendAsync(deleteApproved);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The PENDING passkey itself can be deleted at any time (e.g. "reject").
        using (var deletePending = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/credentials/{pendingId}"))
        {
            deletePending.Headers.Add("X-Session-Token", token);
            var response = await _client.SendAsync(deletePending);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var ownerId = await _factory.WithDbAsync(async db =>
            (await db.PasskeyCredentials.SingleAsync(c => c.Id == approvedId)).AuthUserId);
        await _factory.WithDbAsync(async db =>
            Assert.Equal(1, await db.PasskeyCredentials.CountAsync(c => c.AuthUserId == ownerId)));
    }
}

/// <summary>
/// Device linking via code (alternative to the browser-dependent WebAuthn cross-device/hybrid
/// transport, which isn't discoverable depending on browser/OS): an already logged-in device
/// generates a short-lived code (api/auth/link/begin), a NEW, session-less device redeems
/// it via register/begin-linked and registers its OWN local passkey in the process -
/// lands as PENDING just like begin-additional and still needs an explicit approval.
/// </summary>
public class DeviceLinkTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DeviceLinkTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LinkBegin_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.PostAsync("/api/auth/link/begin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterBeginLinked_WithInvalidCode_ReturnsBadRequest()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.PostAsJsonAsync("/api/auth/register/begin-linked",
            new DeviceLinkRedeemRequestDto { Code = "0000-0000" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeviceLink_HappyPath_NewDeviceRegistersPendingThenApprovedThenLogsIn()
    {
        using var firstKey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);

        // ── Already-logged-in device generates a code ─────────────────────────────────
        using var linkBeginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/link/begin");
        linkBeginRequest.Headers.Add("X-Session-Token", token);
        var linkBeginResponse = await _client.SendAsync(linkBeginRequest);
        Assert.Equal(HttpStatusCode.OK, linkBeginResponse.StatusCode);
        var linkDto = await linkBeginResponse.Content.ReadFromJsonAsync<DeviceLinkCodeResponseDto>();
        Assert.NotNull(linkDto);
        Assert.NotEmpty(linkDto!.Code);
        Assert.True(linkDto.ExpiresInSeconds > 0);

        // ── NEW, session-less device redeems the code and registers locally ─────────────
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var beginLinkedResponse = await anon.PostAsJsonAsync("/api/auth/register/begin-linked",
            new DeviceLinkRedeemRequestDto { Code = linkDto.Code });
        Assert.Equal(HttpStatusCode.OK, beginLinkedResponse.StatusCode);
        var options = await beginLinkedResponse.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(options);

        using var secondKey = new FakePasskey();
        var completeResponse = await PasskeyHttp.CompleteAsync(anon, "/api/auth/register/complete",
            options!.OptionsId, secondKey.CreateAttestationResponse(options.OptionsJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completeDto = await completeResponse.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.True(completeDto!.Pending);
        Assert.Null(completeDto.Token);
        Assert.Equal("Alex", completeDto.DisplayName);

        var credentialId = await _factory.WithDbAsync(async db =>
        {
            var cred = await db.PasskeyCredentials.SingleAsync(c => c.CredentialId == secondKey.CredentialId);
            Assert.Null(cred.ApprovedAt);
            var owner = await db.AuthUsers.SingleAsync(u => u.Id == cred.AuthUserId);
            Assert.Equal("Alex", owner.DisplayName);
            return cred.Id;
        });

        // ── The same code is now consumed (single-use after success) ─────────────────────
        var reuseResponse = await anon.PostAsJsonAsync("/api/auth/register/begin-linked",
            new DeviceLinkRedeemRequestDto { Code = linkDto.Code });
        Assert.Equal(HttpStatusCode.BadRequest, reuseResponse.StatusCode);

        // ── Login with the new, not-yet-approved passkey: pending_approval ─────────
        var (loginId, loginJson) = await PasskeyHttp.BeginAsync(anon, "/api/auth/login/begin");
        var deniedLogin = await PasskeyHttp.CompleteAsync(anon, "/api/auth/login/complete", loginId,
            secondKey.CreateAssertionResponse(loginJson, PasskeyHttp.Origin, signCount: 2));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);

        // ── Approval by the original, logged-in device ─────────────────────────────
        using (var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/credentials/{credentialId}/approve"))
        {
            approve.Headers.Add("X-Session-Token", token);
            var approveResponse = await _client.SendAsync(approve);
            Assert.Equal(HttpStatusCode.NoContent, approveResponse.StatusCode);
        }

        // ── Login with the now-approved passkey from the new device: successful ────────────
        var (loginId2, loginJson2) = await PasskeyHttp.BeginAsync(anon, "/api/auth/login/begin");
        var okLogin = await PasskeyHttp.CompleteAsync(anon, "/api/auth/login/complete", loginId2,
            secondKey.CreateAssertionResponse(loginJson2, PasskeyHttp.Origin, signCount: 3));
        Assert.Equal(HttpStatusCode.OK, okLogin.StatusCode);
        var okDto = await okLogin.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(okDto?.Token);
    }
}

/// <summary>
/// GET /api/auth/account-info (AuthController.GetAccountInfo) - IsOwner=true only for the first
/// registered user. The client uses this to hide the backup/restore UI for all other users,
/// instead of letting them run into a 403 from BackupController.IsOwnerAsync.
/// </summary>
public class AccountInfoTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AccountInfoTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAccountInfo_FirstRegisteredUser_IsOwner_SecondUser_IsNot()
    {
        using var firstKey = new FakePasskey();
        var ownerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var nonOwnerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        async Task<AccountInfoDto?> GetAccountInfoAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/account-info");
            request.Headers.Add("X-Session-Token", token);
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<AccountInfoDto>();
        }

        Assert.True((await GetAccountInfoAsync(ownerToken))!.IsOwner);
        Assert.False((await GetAccountInfoAsync(nonOwnerToken))!.IsOwner);
    }

    [Fact]
    public async Task GetAccountInfo_WithoutSession_ReturnsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.GetAsync("/api/auth/account-info");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Setup-secret gate for the very first passkey registration ever (AuthController.
/// RegisterBegin + SystemSecretsService): prevents anyone from hijacking the owner role
/// on a fresh, network-reachable installation before the actual operator does. As soon as any
/// passkey exists, registration is wide open again. Four separate test classes instead of
/// [Fact]s in a shared one - each case needs a truly pristine DB without an existing passkey,
/// a shared IClassFixture would otherwise make them share one.
/// </summary>
public class RegisterBegin_WithoutSetupSecretTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public RegisterBegin_WithoutSetupSecretTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task RegisterBegin_FirstEverRegistration_WithoutSetupSecret_ReturnsUnauthorized()
    {
        // No PasskeyHttp.BeginAsync here - the helper internally asserts OK, but this test case
        // needs exactly the negative outcome.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register/begin")
        {
            Content = JsonContent.Create(new PasskeyRegisterBeginRequestDto { DisplayName = "Alex" }),
        };
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class RegisterBegin_WithWrongSetupSecretTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public RegisterBegin_WithWrongSetupSecretTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task RegisterBegin_FirstEverRegistration_WithWrongSetupSecret_ReturnsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register/begin")
        {
            Content = JsonContent.Create(new PasskeyRegisterBeginRequestDto
            {
                DisplayName = "Alex",
                SetupSecret = "FALSCH-CODE",
            }),
        };
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class RegisterBegin_WithCorrectSetupSecretTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegisterBegin_WithCorrectSetupSecretTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterBegin_FirstEverRegistration_WithCorrectSetupSecret_Succeeds()
    {
        // The complete flow (begin → complete) via the helper implicitly also covers that
        // register/begin returns OK instead of Unauthorized with the correct code.
        using var key = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", key);
        Assert.NotEmpty(token);
    }
}

public class RegisterBegin_SecondRegistrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegisterBegin_SecondRegistrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterBegin_SecondRegistration_NeedsNoSetupSecret()
    {
        using var firstKey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);

        // Second user WITHOUT any SetupSecret in the request - open family signup stays
        // unchanged for all registrations AFTER the very first one.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register/begin")
        {
            Content = JsonContent.Create(new PasskeyRegisterBeginRequestDto { DisplayName = "Anna" }),
        };
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
