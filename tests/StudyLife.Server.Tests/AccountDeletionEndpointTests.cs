// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>Shared plumbing for the account-deletion endpoint tests: real passkey registrations,
/// session ageing (a session counts as "fresh" for two minutes after sign-in) and the re-authentication
/// proof bodies.</summary>
internal sealed class AccountHttp(CustomWebApplicationFactory factory)
{
    private static int _signCounter = 100;
    // xUnit builds a new test-class instance (and so a new AccountHttp) per test, but the factory -
    // and with it the one real owner - lives for the whole class: the owner state is per factory.
    private sealed class OwnerState
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public (string Token, FakePasskey Passkey)? Owner;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CustomWebApplicationFactory, OwnerState> Owners = new();

    public HttpClient Client { get; } = ApiKeyTestHelpers.CreateClientWithKey(factory, null);

    /// <summary>The first registration claims the migration-seeded user 1, the owner. Done once.</summary>
    public async Task<(string Token, FakePasskey Passkey)> OwnerAsync()
    {
        var state = Owners.GetOrCreateValue(factory);
        await state.Lock.WaitAsync();
        try
        {
            if (state.Owner is null)
            {
                var passkey = new FakePasskey();
                state.Owner = (await PasskeyHttp.RegisterAsync(factory, Client, "Owner", passkey), passkey);
            }
            return state.Owner.Value;
        }
        finally { state.Lock.Release(); }
    }

    /// <summary>A further, non-owner account with its own passkey.</summary>
    public async Task<(string Token, FakePasskey Passkey, int UserId)> NewUserAsync(string name)
    {
        await OwnerAsync();
        var passkey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(factory, Client, name, passkey);
        var userId = await factory.WithDbAsync(async db =>
        {
            var hash = AuthSessionService.HashToken(token);
            return await db.AuthSessions.Where(s => s.TokenHash == hash).Select(s => s.AuthUserId).SingleAsync();
        });
        return (token, passkey, userId);
    }

    /// <summary>Pretends the sign-in of this session was an hour ago, so only an explicit proof counts.</summary>
    public Task AgeAsync(string token) => factory.WithDbAsync(async db =>
    {
        var hash = AuthSessionService.HashToken(token);
        await db.AuthSessions.Where(s => s.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IssuedAt, DateTime.UtcNow.AddHours(-1)));
    });

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(AuthSessionService.TokenHeaderName, token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return Client.SendAsync(request);
    }

    /// <summary>Challenge from the server, signed by the given passkey - the body of a delete request.</summary>
    public async Task<JsonObject> PasskeyProofAsync(string token, FakePasskey passkey)
    {
        var (optionsId, optionsJson) = await PasskeyHttp.BeginAsync(Client, "/api/auth/account/reauth/begin", null, token);
        return new JsonObject
        {
            ["optionsId"] = optionsId,
            ["response"] = passkey.CreateAssertionResponse(optionsJson, PasskeyHttp.Origin, (uint)Interlocked.Increment(ref _signCounter)),
        };
    }

    public async Task<List<string>> RecoveryCodesAsync(string token)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecoveryCodesResponseDto>())!.Codes;
    }

    public Task<bool> UserExistsAsync(int userId) =>
        factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == userId));
}

/// <summary>DELETE api/auth/account: who may call it and what counts as a fresh proof. A deletion here
/// is seven requests at most - the endpoint sits in a strict rate-limit partition (10 per 15 min), so
/// the recovery-code and policy cases live in their own classes with their own factory.</summary>
public class AccountDeletionReauthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly AccountHttp _http;

    public AccountDeletionReauthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _http = new AccountHttp(factory);
    }

    [Fact]
    public async Task ItNeedsASession_NotAnApiKeyAndNotNothing()
    {
        var (token, _, userId) = await _http.NewUserAsync("Robin");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.Client.DeleteAsync("/api/auth/account")).StatusCode);

        // An integration key of the very same user must not be able to delete the account.
        var generated = await _http.SendAsync(HttpMethod.Post, "/api/settings/ha-api-key/generate", token);
        var apiKey = (await generated.Content.ReadFromJsonAsync<JsonObject>())!["apiKey"]!.GetValue<string>();
        using var withKey = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);
        var viaKey = await withKey.DeleteAsync("/api/auth/account");
        Assert.True(viaKey.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"was {viaKey.StatusCode}");

        Assert.True(await _http.UserExistsAsync(userId));
    }

    [Fact]
    public async Task AnOldSessionWithoutProof_IsRefusedWith400_AndNothingIsDeleted()
    {
        var (token, _, userId) = await _http.NewUserAsync("Sam");
        await _http.AgeAsync(token);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _http.UserExistsAsync(userId));
        // The session is still alive: a refused proof is not a logout.
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", token)).StatusCode);
    }

    [Fact]
    public async Task ASessionThatJustSignedIn_IsItsOwnFreshProof()
    {
        var (token, _, userId) = await _http.NewUserAsync("Taylor");

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<AccountDeletionResultDto>())!.InstallationReset);
        Assert.False(await _http.UserExistsAsync(userId));
    }

    [Fact]
    public async Task APasskeyAssertion_DeletesTheAccount_AndTheSessionDiesWithIt()
    {
        var (token, passkey, userId) = await _http.NewUserAsync("Uma");
        await _http.AgeAsync(token);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token, await _http.PasskeyProofAsync(token, passkey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await _http.UserExistsAsync(userId));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.PasskeyCredentials.CountAsync(c => c.AuthUserId == userId)));
        // The token is worthless immediately, also on the pod that cached it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", token)).StatusCode);
    }

    [Fact]
    public async Task AnotherUsersPasskey_ProvesNothing()
    {
        var (token, _, userId) = await _http.NewUserAsync("Val");
        var (_, otherPasskey, _) = await _http.NewUserAsync("Wren");
        await _http.AgeAsync(token);

        // A valid assertion - but from somebody else's authenticator.
        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token, await _http.PasskeyProofAsync(token, otherPasskey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauthentication-failed", (await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        Assert.True(await _http.UserExistsAsync(userId));
    }

    [Fact]
    public async Task AChallengeIssuedToSomeoneElse_IsRejected_AndAChallengeWorksOnce()
    {
        var (tokenA, passkeyA, userA) = await _http.NewUserAsync("Xen");
        var (tokenB, passkeyB, userB) = await _http.NewUserAsync("Yael");
        await _http.AgeAsync(tokenA);
        await _http.AgeAsync(tokenB);

        // B's own, valid proof presented in A's request: the challenge is bound to B's session user.
        var proofOfB = await _http.PasskeyProofAsync(tokenB, passkeyB);
        var crossed = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", tokenA, proofOfB);
        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);
        Assert.True(await _http.UserExistsAsync(userA));

        // That attempt consumed the challenge: even B cannot use it any more.
        var replay = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", tokenB, proofOfB);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.True(await _http.UserExistsAsync(userB));
        _ = passkeyA;
    }

    [Fact]
    public async Task ReauthBegin_NeedsASession_AndAnAccountWithAPasskey()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.Client.PostAsync("/api/auth/account/reauth/begin", null)).StatusCode);

        // The migration-seeded user has no passkey until somebody registers: nothing to confirm with.
        var (token, _, _) = await _http.NewUserAsync("Zed");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Post, "/api/auth/account/reauth/begin", token)).StatusCode);
    }
}

/// <summary>Recovery codes as the proof, the input bounds, and the order "policy before proof".</summary>
public class AccountDeletionRecoveryCodeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly AccountHttp _http;

    public AccountDeletionRecoveryCodeTests(CustomWebApplicationFactory factory) => _http = new AccountHttp(factory);

    [Fact]
    public async Task AnOwnRecoveryCode_DeletesTheAccount_ForeignOrWrongCodesDoNot()
    {
        var (token, _, userId) = await _http.NewUserAsync("Ari");
        var (otherToken, _, otherId) = await _http.NewUserAsync("Bao");
        var ownCodes = await _http.RecoveryCodesAsync(token);
        var foreignCodes = await _http.RecoveryCodesAsync(otherToken);
        await _http.AgeAsync(token);

        foreach (var wrong in new[] { "AAAA-BBBB-CCCC", foreignCodes[0] })
        {
            var refused = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token, new JsonObject { ["recoveryCode"] = wrong });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        Assert.True(await _http.UserExistsAsync(userId));
        // The foreign code was not spent by the failed attempt.
        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/recovery/status", otherToken)).Content.ReadFromJsonAsync<RecoveryStatusDto>();
        Assert.Equal(8, status!.UnusedCount);

        var accepted = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token, new JsonObject { ["recoveryCode"] = ownCodes[0] });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False(await _http.UserExistsAsync(userId));
        Assert.True(await _http.UserExistsAsync(otherId));
    }

    [Fact]
    public async Task ARecoveryCode_IsNotSpentOnARequestThePolicyRefuses()
    {
        var (ownerToken, _) = await _http.OwnerAsync();
        await _http.NewUserAsync("Cy"); // the owner now has company, so they cannot leave
        var codes = await _http.RecoveryCodesAsync(ownerToken);
        await _http.AgeAsync(ownerToken);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", ownerToken, new JsonObject { ["recoveryCode"] = codes[0] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/recovery/status", ownerToken)).Content.ReadFromJsonAsync<RecoveryStatusDto>();
        Assert.Equal(8, status!.UnusedCount);
    }

    [Fact]
    public async Task OversizedProofFields_AreRejectedWith400()
    {
        var (token, _, userId) = await _http.NewUserAsync("Dee");
        await _http.AgeAsync(token);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token,
            new JsonObject { ["recoveryCode"] = new string('A', 51) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _http.UserExistsAsync(userId));
    }
}

/// <summary>The owner rules and the owner's "remove user" action over HTTP.</summary>
public class AccountDeletionOwnerPolicyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly AccountHttp _http;

    public AccountDeletionOwnerPolicyTests(CustomWebApplicationFactory factory) => _http = new AccountHttp(factory);

    [Fact]
    public async Task TheOwnerCannotDeleteWhileOthersExist_AndThePreviewSaysWhy()
    {
        var (ownerToken, _) = await _http.OwnerAsync();
        var (guestToken, _, _) = await _http.NewUserAsync("Eli");

        var preview = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account/deletion-preview", ownerToken))
            .Content.ReadFromJsonAsync<AccountDeletionPreviewDto>();
        Assert.False(preview!.Available, System.Text.Json.JsonSerializer.Serialize(preview));
        Assert.Equal("owner-has-other-users", preview.UnavailableReason);
        Assert.True(preview.IsOwner);
        Assert.True(preview.OtherUserCount >= 1);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", ownerToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("owner-has-other-users", problem!["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", ownerToken)).StatusCode);

        var guestPreview = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account/deletion-preview", guestToken))
            .Content.ReadFromJsonAsync<AccountDeletionPreviewDto>();
        Assert.True(guestPreview!.Available);
        Assert.Null(guestPreview.UnavailableReason);
        Assert.False(guestPreview.IsOwner);
    }

    [Fact]
    public async Task TheOwnerRemovesAnotherUser_WithTheirOwnFreshProof()
    {
        var (ownerToken, ownerPasskey) = await _http.OwnerAsync();
        var (guestToken, _, guestId) = await _http.NewUserAsync("Fay");
        await _http.AgeAsync(ownerToken);

        var list = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/users", ownerToken)).Content.ReadFromJsonAsync<List<UserListItemDto>>();
        Assert.Contains(list!, u => u.Id == guestId && u.DisplayName == "Fay");
        Assert.DoesNotContain(list!, u => u.DisplayName == "Owner");

        // Without a proof the owner's old session is not enough ...
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(HttpMethod.Delete, $"/api/auth/users/{guestId}", ownerToken)).StatusCode);
        Assert.True(await _http.UserExistsAsync(guestId));

        // ... with it, the guest's account is gone through the same use case, and so is their session.
        var proof = await _http.PasskeyProofAsync(ownerToken, ownerPasskey);
        var removed = await _http.SendAsync(HttpMethod.Delete, $"/api/auth/users/{guestId}", ownerToken, proof);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(await _http.UserExistsAsync(guestId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", guestToken)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerListsAndRemovesUsers_AndNotThemselvesThroughThatDoor()
    {
        var (ownerToken, _) = await _http.OwnerAsync();
        var (guestToken, _, guestId) = await _http.NewUserAsync("Gus");
        var (_, _, victimId) = await _http.NewUserAsync("Hal");

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(HttpMethod.Get, "/api/auth/users", guestToken)).StatusCode);
        var notOwner = await _http.SendAsync(HttpMethod.Delete, $"/api/auth/users/{victimId}", guestToken);
        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        Assert.Equal("owner-only", (await notOwner.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        Assert.True(await _http.UserExistsAsync(victimId));

        var info = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", ownerToken)).Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(HttpMethod.Delete, $"/api/auth/users/{info!.UserId}", ownerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(HttpMethod.Delete, "/api/auth/users/987654", ownerToken)).StatusCode);
        // A non-owner gets 403 for an unknown id too, so ids cannot be probed.
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(HttpMethod.Delete, "/api/auth/users/987654", guestToken)).StatusCode);
        Assert.True(await _http.UserExistsAsync(guestId));
    }
}

/// <summary>The owner as the last account: everything goes, and the installation behaves like a
/// fresh one. Own factory - it empties the database of users.</summary>
public class AccountDeletionFirstRunTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly AccountHttp _http;

    public AccountDeletionFirstRunTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _http = new AccountHttp(factory);
    }

    [Fact]
    public async Task TheLastOwnerDeleting_ReturnsTheInstallationToFirstRun()
    {
        var (ownerToken, _) = await _http.OwnerAsync();
        await _factory.WithDbAsync(async db =>
        {
            db.Notes.Add(new NoteEntity { AuthUserId = 1, Title = "owner's note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        var preview = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account/deletion-preview", ownerToken))
            .Content.ReadFromJsonAsync<AccountDeletionPreviewDto>();
        Assert.True(preview!.Available);
        Assert.True(preview.InstallationWillReset);

        var response = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", ownerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<AccountDeletionResultDto>())!.InstallationReset);

        // No owner, no passkey, no session, no invite is left ...
        await _factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.AuthUsers.ToListAsync());
            Assert.Empty(await db.PasskeyCredentials.ToListAsync());
            Assert.Empty(await db.AuthSessions.ToListAsync());
            Assert.Empty(await db.AuthInvites.ToListAsync());
            Assert.Empty(await db.RecoveryCodes.ToListAsync());
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", ownerToken)).StatusCode);

        // ... the login screen sees a fresh install (400 = no passkey registered yet) ...
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.Client.PostAsync("/api/auth/login/begin", null)).StatusCode);

        // ... a new setup code was issued, so the bootstrap flow works exactly as on first run ...
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<SystemSecretsService>();
            var code = await secrets.EnsureSetupSecretAsync();
            Assert.True(await secrets.ValidateSetupSecretAsync(code));
            var wrongStart = await _http.Client.PostAsJsonAsync("/api/auth/register/begin",
                new PasskeyRegisterBeginRequestDto { DisplayName = "Intruder", SetupSecret = "WRONG-CODE" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongStart.StatusCode);
        }

        // ... and whoever registers with it becomes the one and only owner, with nothing inherited.
        var newToken = await PasskeyHttp.RegisterAsync(_factory, _http.Client, "New owner", new FakePasskey());
        var info = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", newToken)).Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.True(info!.IsOwner);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuthUsers.CountAsync()));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync())); // the old owner's note is gone
    }
}

/// <summary>Demo instances refuse every write, deletion included, and say so in the preview.</summary>
public class AccountDeletionDemoModeTests : IClassFixture<AuthControllerDemoModeTests.DemoModeFactory>
{
    private readonly HttpClient _client;

    public AccountDeletionDemoModeTests(AuthControllerDemoModeTests.DemoModeFactory factory) =>
        _client = ApiKeyTestHelpers.CreateClientWithKey(factory, null);

    [Fact]
    public async Task OnADemoInstance_TheDeleteIsBlocked_AndThePreviewIsUnavailable()
    {
        var login = await _client.PostAsync("/api/auth/demo-login", null);
        var token = (await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account");
        delete.Headers.Add("X-Session-Token", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(delete)).StatusCode);

        using var preview = new HttpRequestMessage(HttpMethod.Get, "/api/auth/account/deletion-preview");
        preview.Headers.Add("X-Session-Token", token);
        var dto = await (await _client.SendAsync(preview)).Content.ReadFromJsonAsync<AccountDeletionPreviewDto>();
        Assert.False(dto!.Available);
        Assert.Equal("demo", dto.UnavailableReason);
    }
}

/// <summary>The recovery code is a possible proof, so the delete endpoints sit in a strict rate-limit
/// partition: a stolen session cannot guess codes at the generic 300 requests a minute.</summary>
public class AccountDeletionRateLimitTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly AccountHttp _http;

    public AccountDeletionRateLimitTests(CustomWebApplicationFactory factory) => _http = new AccountHttp(factory);

    [Fact]
    public async Task GuessingRecoveryCodesThroughTheDeleteEndpoint_IsThrottledAfterTenAttempts()
    {
        var (token, _, userId) = await _http.NewUserAsync("Ida");
        await _http.AgeAsync(token);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", token,
                new JsonObject { ["recoveryCode"] = "AAAA-BBBB-CCCC" })).StatusCode);

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Forbidden, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
        Assert.True(await _http.UserExistsAsync(userId));
    }
}
