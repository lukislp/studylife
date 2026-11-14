// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SignIn;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// HTTP helpers for the password endpoints. Every factory is one TestServer whose requests all share
/// one client-IP partition, so a test class stays below the password partitions (10 sign-ins and 20
/// management calls per 15 minutes) - the limits themselves are pinned in their own class.
/// </summary>
internal sealed class PasswordHttp(CustomWebApplicationFactory factory)
{
    public const string Strong = "violet harbour lantern 7";
    public const string Stronger = "quiet meadow compass 19";

    public HttpClient Anonymous { get; } = ApiKeyTestHelpers.CreateClientWithKey(factory, null);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Add(AuthSessionService.TokenHeaderName, token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return Anonymous.SendAsync(request);
    }

    public Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        Anonymous.PostAsJsonAsync("/api/auth/password/login", new PasswordLoginRequestDto { Username = username, Password = password });

    public async Task<string> LoginTokenAsync(string username, string password)
    {
        var response = await LoginAsync(username, password);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"login: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;
    }

    public async Task<string> SetupSecretAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SystemSecretsService>().EnsureSetupSecretAsync();
    }

    public Task<HttpResponseMessage> RegisterAsync(string displayName, string username, string password, string? setupSecret = null, string? invite = null) =>
        Anonymous.PostAsJsonAsync("/api/auth/password/register", new PasswordRegisterRequestDto
        {
            DisplayName = displayName,
            Username = username,
            Password = password,
            SetupSecret = setupSecret,
            InviteToken = invite,
        });

    /// <summary>A session as old as an hour, so only an explicit proof counts as fresh.</summary>
    public Task<string> AgedSessionAsync(int userId) => factory.WithDbAsync(async db =>
    {
        var token = AuthSessionService.IssueSession(db, userId, DateTime.UtcNow);
        await db.SaveChangesAsync();
        var hash = AuthSessionService.HashToken(token);
        await db.AuthSessions.Where(s => s.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IssuedAt, DateTime.UtcNow.AddHours(-1)));
        return token;
    });

    public async Task<string> RecoveryCodeAsync(string token)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/recovery/generate", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecoveryCodesResponseDto>())!.Codes[0];
    }

    public static async Task<string?> ProblemTypeAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task AddApprovedPasskeyAsync(int userId) => factory.WithDbAsync(async db =>
    {
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            AuthUserId = userId,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey = [1, 2, 3],
            CreatedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });

    /// <summary>Gives an account a password straight through the service (no HTTP, no rate limit).</summary>
    public Task GivePasswordAsync(int userId, string username, string password) => factory.WithDbAsync(async db =>
    {
        var credential = new PasswordCredentialEntity
        {
            AuthUserId = userId,
            Username = username,
            NormalizedUsername = UsernamePolicy.Key(username),
            PasswordHash = new IdentityPasswordHashing().Hash(PasswordPolicy.Normalize(password)!),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PasswordCredentials.Add(credential);
        await db.SaveChangesAsync();
    });

    /// <summary>A plain extra account without any credential (no HTTP, no rate limit).</summary>
    public Task<int> NewUserAsync(string displayName) => factory.WithDbAsync(async db =>
    {
        var user = new AuthUserEntity { DisplayName = displayName, CreatedAt = DateTime.UtcNow };
        db.AuthUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });

    /// <summary>Claims the installation (user 1 gets a password) unless something already did, so
    /// later registrations need no setup code - independent of the order xUnit picks.</summary>
    public async Task EnsureClaimedAsync()
    {
        if (!await factory.WithDbAsync(db => InstanceBootstrap.IsClaimedAsync(db)))
            await GivePasswordAsync(1, "seed.owner", Strong);
    }

    public Task<int> UserIdOfAsync(string token) => factory.WithDbAsync(async db =>
    {
        var hash = AuthSessionService.HashToken(token);
        return await db.AuthSessions.Where(s => s.TokenHash == hash).Select(s => s.AuthUserId).SingleAsync();
    });
}

/// <summary>Adding, using, changing and removing a password on an existing (passkey-era) account.</summary>
public class PasswordLifecycleTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task Methods_OfferPasswordSignInByDefault()
    {
        var methods = await _http.Anonymous.GetFromJsonAsync<AuthMethodsDto>("/api/auth/methods");
        Assert.True(methods!.PasswordSignIn);
    }

    [Fact]
    public async Task Status_RequiresASession()
    {
        var response = await _http.SendAsync(HttpMethod.Get, "/api/auth/password", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddSignInChangeAndRemove_FullLifecycle()
    {
        // The seeded user 1 stands for every account that existed before this feature: no password.
        var session = await _http.AgedSessionAsync(1);
        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", session)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.True(status!.Enabled);
        Assert.False(status.HasPassword);
        Assert.Null(status.Username);

        // Input is checked before the proof (no recovery code is spent on a refused password)...
        var weak = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "Alex.Example", ["newPassword"] = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("password-too-short", await PasswordHttp.ProblemTypeAsync(weak));

        // ...and an old session alone does not add a lasting second way in.
        var noProof = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "Alex.Example", ["newPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);
        Assert.Equal("reauthentication-required", await PasswordHttp.ProblemTypeAsync(noProof));

        var add = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session, new JsonObject
        {
            ["username"] = "  Alex.Example ",
            ["newPassword"] = PasswordHttp.Strong,
            ["recoveryCode"] = await _http.RecoveryCodeAsync(session),
        });
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

        status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", session)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.True(status!.HasPassword);
        Assert.Equal("Alex.Example", status.Username);
        Assert.False(status.CanRemove); // no passkey on this account: the password is its only way in
        Assert.False(status.ChangeRequired);

        // Sign-in: case-insensitive name, same kind of session a passkey sign-in issues.
        var login = await _http.LoginAsync("ALEX.example", PasswordHttp.Strong);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginDto = await login.Content.ReadFromJsonAsync<PasswordLoginResponseDto>();
        Assert.False(loginDto!.PasswordChangeRequired);
        Assert.Equal(43, loginDto.Token!.Length);
        var whoami = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/whoami", loginDto.Token)).Content.ReadFromJsonAsync<WhoamiResponseDto>();
        Assert.Equal(1, whoami!.UserId);
        Assert.Equal("session", whoami.Credential);

        // Wrong password and unknown name look exactly alike.
        var wrong = await _http.LoginAsync("alex.example", PasswordHttp.Stronger);
        var unknown = await _http.LoginAsync("nobody-here", PasswordHttp.Strong);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(StripTraceId(await wrong.Content.ReadAsStringAsync()), StripTraceId(await unknown.Content.ReadAsStringAsync()));

        // Change with the current password as proof: every OTHER session dies, this one stays.
        var change = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["newPassword"] = PasswordHttp.Stronger, ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", loginDto.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.LoginAsync("alex.example", PasswordHttp.Strong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.LoginAsync("Alex.Example", PasswordHttp.Stronger)).StatusCode);

        // The only usable method cannot be removed...
        var refused = await _http.SendAsync(HttpMethod.Delete, "/api/auth/password", session,
            new JsonObject { ["currentPassword"] = PasswordHttp.Stronger });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("last-sign-in-method", await PasswordHttp.ProblemTypeAsync(refused));

        // ...but once the account has a passkey, it can.
        await _http.AddApprovedPasskeyAsync(1);
        var removed = await _http.SendAsync(HttpMethod.Delete, "/api/auth/password", session,
            new JsonObject { ["currentPassword"] = PasswordHttp.Stronger });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", session)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.False(status!.HasPassword);
        Assert.True(status.HasPasskey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.LoginAsync("alex.example", PasswordHttp.Stronger)).StatusCode);
    }

    /// <summary>The problem body carries the request's own trace id; everything else must match.</summary>
    private static string StripTraceId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");
}


/// <summary>Proofs and refusals of the step-up check around the password endpoints.</summary>
public class PasswordStepUpTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task WrongCurrentPassword_IsRefused_FreshSignInIsAccepted()
    {
        var userId = await _http.NewUserAsync("First");
        await _http.GivePasswordAsync(userId, "first.user", PasswordHttp.Strong);
        var aged = await _http.AgedSessionAsync(userId);

        var wrong = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", aged,
            new JsonObject { ["newPassword"] = PasswordHttp.Stronger, ["currentPassword"] = "not the password" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("reauthentication-failed", await PasswordHttp.ProblemTypeAsync(wrong));

        // A session signed in a moment ago counts as the proof (the existing two-minute window).
        var fresh = await _http.LoginTokenAsync("first.user", PasswordHttp.Strong);
        var change = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", fresh,
            new JsonObject { ["newPassword"] = PasswordHttp.Stronger });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        // The aged session was another session of the account: revoked by the change.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", aged)).StatusCode);
    }

    [Fact]
    public async Task UsernameRules_AreEnforcedOnAdd()
    {
        await _http.EnsureClaimedAsync();
        await _http.GivePasswordAsync(await _http.NewUserAsync("Taken"), "taken.name", PasswordHttp.Strong);
        var second = await _http.NewUserAsync("Second");
        var session = await _http.AgedSessionAsync(second);

        var invalid = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "no spaces allowed", ["newPassword"] = PasswordHttp.Strong });
        Assert.Equal("username-invalid", await PasswordHttp.ProblemTypeAsync(invalid));

        var missing = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["newPassword"] = PasswordHttp.Strong });
        Assert.Equal("username-invalid", await PasswordHttp.ProblemTypeAsync(missing));

        var taken = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "TAKEN.name", ["newPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("username-taken", await PasswordHttp.ProblemTypeAsync(taken));

        var containsName = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "marigold", ["newPassword"] = "marigold in the garden" });
        Assert.Equal("password-contains-username", await PasswordHttp.ProblemTypeAsync(containsName));

        var common = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "marigold", ["newPassword"] = "password1234" });
        Assert.Equal("password-common", await PasswordHttp.ProblemTypeAsync(common));

        var tooLong = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["username"] = "marigold", ["newPassword"] = new string('x', 100) + PasswordHttp.Strong + new string('y', 100) });
        Assert.Equal("password-too-long", await PasswordHttp.ProblemTypeAsync(tooLong));
    }

    [Fact]
    public async Task AccountDeletion_AcceptsTheCurrentPasswordAsProof()
    {
        // A fresh non-owner account, so the owner policy cannot get in the way.
        await _http.EnsureClaimedAsync();
        var token = (await (await _http.RegisterAsync("Leaver", "leaver.user", PasswordHttp.Strong)).Content
            .ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;
        var userId = await _http.UserIdOfAsync(token);
        var aged = await _http.AgedSessionAsync(userId);

        var wrong = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", aged, new JsonObject { ["currentPassword"] = PasswordHttp.Stronger });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var deleted = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", aged, new JsonObject { ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.PasswordCredentials.AnyAsync(c => c.AuthUserId == userId)));
    }
}

/// <summary>A password lets an account drop its last passkey; a passkey-only account still cannot.</summary>
public class PasskeyRemovalWithPasswordTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task LastPasskey_MayGo_WhenThePasswordRemains()
    {
        await _http.GivePasswordAsync(1, "keeper", PasswordHttp.Strong);
        await _http.AddApprovedPasskeyAsync(1);
        var id = await factory.WithDbAsync(db => db.PasskeyCredentials.Where(c => c.AuthUserId == 1).Select(c => c.Id).SingleAsync());

        var session = await _http.AgedSessionAsync(1);
        var response = await _http.SendAsync(HttpMethod.Delete, $"/api/auth/credentials/{id}", session);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Now the password is the last method and stays.
        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", session)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.False(status!.CanRemove);
    }
}

/// <summary>Registering a new account with a username and password: same gates as the passkey path.</summary>
public class PasswordRegistrationTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task FirstRegistration_ClaimsTheInstance_AndLaterOnesCreateNewAccounts()
    {
        // Fresh installation: the setup code is required, exactly as for the first passkey.
        var noCode = await _http.RegisterAsync("Owner", "owner.user", PasswordHttp.Strong);
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);

        var first = await _http.RegisterAsync("Owner", "owner.user", PasswordHttp.Strong, await _http.SetupSecretAsync());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var ownerToken = (await first.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;
        var info = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", ownerToken)).Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.Equal(1, info!.UserId); // claimed the migration-seeded legacy user with its data
        Assert.True(info.IsOwner);

        // Claimed now - although no passkey exists, a passkey registration must not be treated as
        // the first one (it would otherwise need the setup code and claim the owner's account).
        using (var passkeyBegin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register/begin")
        {
            Content = JsonContent.Create(new PasskeyRegisterBeginRequestDto { DisplayName = "Guest" }),
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await _http.Anonymous.SendAsync(passkeyBegin)).StatusCode);
        }

        var duplicate = await _http.RegisterAsync("Copycat", "OWNER.user", PasswordHttp.Stronger);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("username-taken", await PasswordHttp.ProblemTypeAsync(duplicate));

        var weak = await _http.RegisterAsync("Weak", "weak.user", "qwerty123456");
        Assert.Equal("password-common", await PasswordHttp.ProblemTypeAsync(weak));

        var badName = await _http.RegisterAsync("Bad", "a b", PasswordHttp.Strong);
        Assert.Equal("username-invalid", await PasswordHttp.ProblemTypeAsync(badName));

        var noName = await _http.RegisterAsync("", "nameless", PasswordHttp.Strong);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var second = await _http.RegisterAsync("Anna", "anna", PasswordHttp.Stronger);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var annaToken = (await second.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;
        var annaInfo = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", annaToken)).Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.NotEqual(1, annaInfo!.UserId);
        Assert.False(annaInfo.IsOwner);
    }
}

/// <summary>Registration:Mode=invite applies to password registrations as well.</summary>
public class PasswordRegistrationInviteTests(PasswordRegistrationInviteTests.InviteFactory factory)
    : IClassFixture<PasswordRegistrationInviteTests.InviteFactory>
{
    public class InviteFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Registration:Mode", "invite");
        }
    }

    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task AfterTheFirstAccount_AnInviteIsRequired_AndConsumed()
    {
        var owner = await _http.RegisterAsync("Owner", "owner.user", PasswordHttp.Strong, await _http.SetupSecretAsync());
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var ownerToken = (await owner.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;

        var withoutInvite = await _http.RegisterAsync("Guest", "guest.user", PasswordHttp.Stronger);
        Assert.Equal(HttpStatusCode.Forbidden, withoutInvite.StatusCode);
        Assert.Equal("invite_required", (await withoutInvite.Content.ReadFromJsonAsync<RegistrationGateErrorDto>())!.Reason);

        var invite = await (await _http.SendAsync(HttpMethod.Post, "/api/auth/invites", ownerToken)).Content.ReadFromJsonAsync<CreateInviteResponseDto>();
        var withInvite = await _http.RegisterAsync("Guest", "guest.user", PasswordHttp.Stronger, invite: invite!.Token);
        Assert.Equal(HttpStatusCode.OK, withInvite.StatusCode);

        var reused = await _http.RegisterAsync("Other", "other.user", PasswordHttp.Stronger, invite: invite.Token);
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        Assert.Equal("invite_invalid", (await reused.Content.ReadFromJsonAsync<RegistrationGateErrorDto>())!.Reason);
    }
}

/// <summary>The operator switch PasswordSignIn:Enabled=false.</summary>
public class PasswordSignInDisabledTests(PasswordSignInDisabledTests.DisabledFactory factory)
    : IClassFixture<PasswordSignInDisabledTests.DisabledFactory>
{
    public class DisabledFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PasswordSignIn:Enabled", "false");
        }
    }

    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task SwitchedOff_EveryPasswordPathIsRefused_ButStoredPasswordsStay()
    {
        await _http.GivePasswordAsync(1, "kept.user", PasswordHttp.Strong);

        Assert.False((await _http.Anonymous.GetFromJsonAsync<AuthMethodsDto>("/api/auth/methods"))!.PasswordSignIn);

        var login = await _http.LoginAsync("kept.user", PasswordHttp.Strong);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("password-sign-in-disabled", await PasswordHttp.ProblemTypeAsync(login));

        var register = await _http.RegisterAsync("New", "new.user", PasswordHttp.Stronger);
        Assert.Equal("password-sign-in-disabled", await PasswordHttp.ProblemTypeAsync(register));

        var session = await _http.AgedSessionAsync(1);
        var set = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", session,
            new JsonObject { ["newPassword"] = PasswordHttp.Stronger, ["recoveryCode"] = "AAAA-BBBB-CCCC" });
        Assert.Equal("password-sign-in-disabled", await PasswordHttp.ProblemTypeAsync(set));

        // The current password is no proof while passwords are off.
        var prover = await _http.NewUserAsync("Prover");
        await _http.GivePasswordAsync(prover, "prover.user", PasswordHttp.Strong);
        var proof = await _http.SendAsync(HttpMethod.Delete, "/api/auth/account", await _http.AgedSessionAsync(prover),
            new JsonObject { ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.Forbidden, proof.StatusCode);
        Assert.Equal("reauthentication-failed", await PasswordHttp.ProblemTypeAsync(proof));

        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", session)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.False(status!.Enabled);
        Assert.True(status.HasPassword); // kept, unusable until switched on again
        Assert.True(status.CanRemove);   // an unusable password never blocks its own removal
    }

    [Fact]
    public async Task SwitchedOff_APasswordDoesNotCountAsTheRemainingMethod()
    {
        var token = await factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = "PasskeyAndPassword", CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        await _http.GivePasswordAsync(token, "both.user", PasswordHttp.Strong);
        await _http.AddApprovedPasskeyAsync(token);
        var passkeyId = await factory.WithDbAsync(db => db.PasskeyCredentials.Where(c => c.AuthUserId == token).Select(c => c.Id).SingleAsync());

        var session = await _http.AgedSessionAsync(token);
        var response = await _http.SendAsync(HttpMethod.Delete, $"/api/auth/credentials/{passkeyId}", session);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SwitchedOff_PasswordCanStillBeRemoved()
    {
        var userId = await factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = "Cleaner", CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        await _http.GivePasswordAsync(userId, "cleaner.user", PasswordHttp.Strong);
        var session = await _http.AgedSessionAsync(userId);
        var removed = await _http.SendAsync(HttpMethod.Delete, "/api/auth/password", session,
            new JsonObject { ["recoveryCode"] = await _http.RecoveryCodeAsync(session) });
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }
}

/// <summary>The owner's temporary password for another account.</summary>
public class OwnerPasswordResetTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task Owner_SetsATemporaryPassword_ThatRevokesSessionsAndAsksForAChange()
    {
        await _http.GivePasswordAsync(1, "the.owner", PasswordHttp.Strong);
        var ownerSession = await _http.AgedSessionAsync(1);

        var guest = await _http.RegisterAsync("Guest", "guest.user", PasswordHttp.Stronger);
        var guestToken = (await guest.Content.ReadFromJsonAsync<PasswordLoginResponseDto>())!.Token!;
        var guestId = await _http.UserIdOfAsync(guestToken);

        // Only the owner, and never for themselves.
        var byGuest = await _http.SendAsync(HttpMethod.Post, "/api/auth/users/1/password-reset", guestToken, new JsonObject());
        Assert.Equal("owner-only", await PasswordHttp.ProblemTypeAsync(byGuest));
        var self = await _http.SendAsync(HttpMethod.Post, "/api/auth/users/1/password-reset", ownerSession,
            new JsonObject { ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        var missing = await _http.SendAsync(HttpMethod.Post, "/api/auth/users/9999/password-reset", ownerSession,
            new JsonObject { ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var users = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/users", ownerSession)).Content.ReadFromJsonAsync<List<UserListItemDto>>();
        Assert.Equal("guest.user", users!.Single(u => u.Id == guestId).Username);

        var reset = await _http.SendAsync(HttpMethod.Post, $"/api/auth/users/{guestId}/password-reset", ownerSession,
            new JsonObject { ["currentPassword"] = PasswordHttp.Strong });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var result = await reset.Content.ReadFromJsonAsync<PasswordResetResultDto>();
        Assert.Equal("guest.user", result!.Username);
        Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}-[a-z2-9]{5}-[a-z2-9]{5}$", result.TemporaryPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(HttpMethod.Get, "/api/auth/account-info", guestToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.LoginAsync("guest.user", PasswordHttp.Stronger)).StatusCode);

        var login = await _http.LoginAsync("guest.user", result.TemporaryPassword);
        var dto = await login.Content.ReadFromJsonAsync<PasswordLoginResponseDto>();
        Assert.True(dto!.PasswordChangeRequired);

        var change = await _http.SendAsync(HttpMethod.Put, "/api/auth/password", dto.Token,
            new JsonObject { ["newPassword"] = "a brand new phrase 2026", ["currentPassword"] = result.TemporaryPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        var status = await (await _http.SendAsync(HttpMethod.Get, "/api/auth/password", dto.Token)).Content.ReadFromJsonAsync<PasswordStatusDto>();
        Assert.False(status!.ChangeRequired);
    }

    [Fact]
    public async Task Reset_ForAnAccountWithoutPassword_NeedsAUsername()
    {
        var passkeyOnly = await factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = "PasskeyOnly", CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        var ownerSession = await _http.AgedSessionAsync(1);
        var code = await _http.RecoveryCodeAsync(ownerSession);

        var noName = await _http.SendAsync(HttpMethod.Post, $"/api/auth/users/{passkeyOnly}/password-reset", ownerSession,
            new JsonObject { ["recoveryCode"] = code });
        Assert.Equal("username-invalid", await PasswordHttp.ProblemTypeAsync(noName));

        var withName = await _http.SendAsync(HttpMethod.Post, $"/api/auth/users/{passkeyOnly}/password-reset", ownerSession,
            new JsonObject { ["username"] = "rescued.user", ["recoveryCode"] = code });
        Assert.Equal(HttpStatusCode.OK, withName.StatusCode);
        Assert.Equal("rescued.user", (await withName.Content.ReadFromJsonAsync<PasswordResetResultDto>())!.Username);
    }
}

/// <summary>Per-IP limit on the sign-in endpoint (10 per 15 minutes; one partition in the TestServer).</summary>
public class PasswordLoginRateLimitTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly PasswordHttp _http = new(factory);

    [Fact]
    public async Task EleventhAttempt_IsThrottled()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.LoginAsync("nobody", PasswordHttp.Strong)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _http.LoginAsync("nobody", PasswordHttp.Strong)).StatusCode);
    }
}

/// <summary>Per-account lockout and hash upgrade, through the service with a controllable clock.</summary>
public class PasswordCredentialServiceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly PasswordHttp _http = new(factory);

    private static PasswordCredentialService Create(IServiceProvider sp, TimeProvider clock) => new(
        sp.GetRequiredService<StudyLifeDb>(),
        sp.GetRequiredService<IPasswordHashing>(),
        sp.GetRequiredService<IOptionsMonitor<PasswordSignInOptions>>(),
        sp.GetRequiredService<ISignInMethodCatalog>(),
        sp.GetRequiredService<IAuthAccountService>(),
        clock);

    private Task<int> NewUserAsync(string name) => factory.WithDbAsync(async db =>
    {
        var user = new AuthUserEntity { DisplayName = name, CreatedAt = DateTime.UtcNow };
        db.AuthUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });

    [Fact]
    public async Task Lockout_StartsAfterFiveFailures_BlocksEvenTheRightPassword_AndExpires()
    {
        var userId = await NewUserAsync("Target");
        await _http.GivePasswordAsync(userId, "target.user", PasswordHttp.Strong);
        var clock = new Clock();
        using var scope = factory.Services.CreateScope();
        var service = Create(scope.ServiceProvider, clock);

        for (var i = 0; i < 5; i++) Assert.False((await service.SignInAsync("target.user", "wrong guess number " + i)).Succeeded);
        // Five are free: the right password still works and resets the counter.
        Assert.True((await service.SignInAsync("target.user", PasswordHttp.Strong)).Succeeded);

        for (var i = 0; i < 6; i++) Assert.False((await service.SignInAsync("target.user", "wrong guess number " + i)).Succeeded);
        Assert.False((await service.SignInAsync("target.user", PasswordHttp.Strong)).Succeeded); // locked for 30 s

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True((await service.SignInAsync("TARGET.user", PasswordHttp.Strong)).Succeeded);

        var row = await factory.WithDbAsync(db => db.PasswordCredentials.AsNoTracking().SingleAsync(c => c.AuthUserId == userId));
        Assert.Equal(0, row.FailedAttemptCount);
        Assert.Null(row.LockedUntil);
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task StepUpVerification_CountsTowardsTheSameLockout()
    {
        var userId = await NewUserAsync("StepUp");
        await _http.GivePasswordAsync(userId, "stepup.user", PasswordHttp.Strong);
        using var scope = factory.Services.CreateScope();
        var service = Create(scope.ServiceProvider, new Clock());

        for (var i = 0; i < 6; i++) Assert.False(await service.VerifyForUserAsync(userId, "wrong guess " + i));
        Assert.False(await service.VerifyForUserAsync(userId, PasswordHttp.Strong));
        Assert.False(await service.VerifyForUserAsync(await NewUserAsync("NoPassword"), PasswordHttp.Strong));
        Assert.False(await service.VerifyForUserAsync(userId, null));
    }

    [Fact]
    public async Task OlderHash_IsUpgradedOnSignIn_AndNfkcInputMatches()
    {
        var userId = await NewUserAsync("Legacy");
        var older = new Microsoft.AspNetCore.Identity.PasswordHasher<object>(
                Options.Create(new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 100_000 }))
            .HashPassword(new object(), "Passphrase for tests");
        await factory.WithDbAsync(async db =>
        {
            db.PasswordCredentials.Add(new PasswordCredentialEntity
            {
                AuthUserId = userId,
                Username = "legacy.user",
                NormalizedUsername = "legacy.user",
                PasswordHash = older,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        using var scope = factory.Services.CreateScope();
        var service = Create(scope.ServiceProvider, new Clock());

        // Full-width input normalises to the same password (NFKC).
        Assert.True((await service.SignInAsync("legacy.user", "Ｐａｓｓｐｈｒａｓｅ for tests")).Succeeded);
        var upgraded = await factory.WithDbAsync(db => db.PasswordCredentials.AsNoTracking().Where(c => c.AuthUserId == userId).Select(c => c.PasswordHash).SingleAsync());
        Assert.NotEqual(older, upgraded);
        Assert.Equal(PasswordCheck.Success, new IdentityPasswordHashing().Verify(upgraded, "Passphrase for tests"));
    }

    [Fact]
    public async Task InvalidInput_FailsWithoutThrowing()
    {
        using var scope = factory.Services.CreateScope();
        var service = Create(scope.ServiceProvider, new Clock());
        Assert.False((await service.SignInAsync(null, null)).Succeeded);
        Assert.False((await service.SignInAsync("x", PasswordHttp.Strong)).Succeeded);
        Assert.False((await service.SignInAsync("valid.name", new string('a', 200))).Succeeded);
        Assert.Equal(23, service.GenerateTemporaryPassword().Length);
    }
}
