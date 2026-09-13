using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

// The three services extracted from the invite/recovery/account auth partials, exercised
// directly (no HTTP) via the same WithServiceAsync helper as DomainServiceTests. The endpoint
// suites (RecoveryCodeTests, PasskeyAuthTests, RegistrationGateTests, SessionRevocationAndClientKeyTests)
// still pin the wire behavior and every policy/status-code decision, which deliberately stayed
// in the controller; these pin what the services now own.

public class AuthInviteServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    // AuthUserId 1 is the user the AddMultiTenantAuthUserFoundation migration seeds into every
    // freshly migrated temp DB.
    private const int SeededUserId = 1;

    public AuthInviteServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateAsync_ReturnsThePlaintextOnceAndPersistsOnlyItsHash()
    {
        var created = await _factory.WithServiceAsync<IAuthInviteService, CreateInviteResponseDto>(
            (invites, _) => invites.CreateAsync(SeededUserId));

        Assert.False(string.IsNullOrWhiteSpace(created.Token));
        Assert.True(created.ExpiresAt > created.CreatedAt);

        var stored = await _factory.WithDbAsync(db => db.AuthInvites.AsNoTracking().FirstAsync(i => i.Id == created.Id));
        Assert.Equal(AuthSessionService.HashToken(created.Token), stored.TokenHash);
        Assert.NotEqual(created.Token, stored.TokenHash);
        Assert.Equal(SeededUserId, stored.CreatedByUserId);
        Assert.Null(stored.UsedAt);
    }

    [Fact]
    public async Task ListAsync_NeverCarriesTheToken()
    {
        var created = await _factory.WithServiceAsync<IAuthInviteService, CreateInviteResponseDto>(
            (invites, _) => invites.CreateAsync(SeededUserId));

        var listed = await _factory.WithServiceAsync<IAuthInviteService, List<InviteListItemDto>>(
            (invites, _) => invites.ListAsync());

        var row = Assert.Single(listed, i => i.Id == created.Id);
        Assert.Null(row.UsedAt);
        // InviteListItemDto has no token field at all - that is the point; assert the shape
        // stays that way by checking the only thing the setup UI is allowed to see.
        Assert.Equal(created.ExpiresAt, row.ExpiresAt);
    }

    [Fact]
    public async Task DeleteAsync_ExistingThenAgain_IsSuccessThenNotFound()
    {
        var created = await _factory.WithServiceAsync<IAuthInviteService, CreateInviteResponseDto>(
            (invites, _) => invites.CreateAsync(SeededUserId));

        var first = await _factory.WithServiceAsync<IAuthInviteService, ServiceResult>(
            (invites, _) => invites.DeleteAsync(created.Id));
        var second = await _factory.WithServiceAsync<IAuthInviteService, ServiceResult>(
            (invites, _) => invites.DeleteAsync(created.Id));

        Assert.Equal(ServiceOutcome.Success, first.Outcome);
        Assert.Equal(ServiceOutcome.NotFound, second.Outcome);
    }
}

public class AuthRecoveryServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthRecoveryServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GenerateAsync_IssuesEightCodesAndInvalidatesTheOldOnes()
    {
        var first = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryCodesResponseDto>(
            (recovery, _) => recovery.GenerateAsync(SeededUserId));
        Assert.Equal(8, first.Codes.Count);

        var second = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryCodesResponseDto>(
            (recovery, _) => recovery.GenerateAsync(SeededUserId));

        // Regenerating replaces the whole set - the old codes must be gone, not merely extra.
        Assert.Equal(8, await _factory.WithDbAsync(db => db.RecoveryCodes.AsNoTracking().CountAsync(c => c.AuthUserId == SeededUserId)));
        Assert.Empty(first.Codes.Intersect(second.Codes));

        var status = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryStatusDto>(
            (recovery, _) => recovery.GetStatusAsync(SeededUserId));
        Assert.Equal(8, status.TotalCount);
        Assert.Equal(8, status.UnusedCount);
        Assert.NotNull(status.CreatedAt);
    }
}

public class AuthRecoveryServiceLoginTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthRecoveryServiceLoginTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RecoveryLoginAsync_ValidCode_IssuesASessionAndBurnsEveryOlderOne()
    {
        var codes = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryCodesResponseDto>(
            (recovery, _) => recovery.GenerateAsync(SeededUserId));
        // A session that existed before the recovery - the "device I lost" case it has to kill.
        await _factory.WithDbAsync(async db =>
        {
            AuthSessionService.IssueSession(db, SeededUserId, DateTime.UtcNow);
            await db.SaveChangesAsync();
        });

        var result = await _factory.WithServiceAsync<IAuthRecoveryService, PasskeyCompleteResponseDto?>(
            (recovery, _) => recovery.RecoveryLoginAsync(codes.Codes[0]));

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Token));
        // Exactly one session survives: the one this call just issued (2026-09 audit S7).
        var sessions = await _factory.WithDbAsync(db => db.AuthSessions.AsNoTracking()
            .Where(s => s.AuthUserId == SeededUserId).ToListAsync());
        var only = Assert.Single(sessions);
        Assert.Equal(AuthSessionService.HashToken(result.Token!), only.TokenHash);
    }

    [Fact]
    public async Task RecoveryLoginAsync_SameCodeTwice_IsRejectedTheSecondTime()
    {
        var codes = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryCodesResponseDto>(
            (recovery, _) => recovery.GenerateAsync(SeededUserId));

        var first = await _factory.WithServiceAsync<IAuthRecoveryService, PasskeyCompleteResponseDto?>(
            (recovery, _) => recovery.RecoveryLoginAsync(codes.Codes[1]));
        var second = await _factory.WithServiceAsync<IAuthRecoveryService, PasskeyCompleteResponseDto?>(
            (recovery, _) => recovery.RecoveryLoginAsync(codes.Codes[1]));

        Assert.NotNull(first);
        // Null, exactly like an unknown code - the two must stay indistinguishable.
        Assert.Null(second);
    }

    [Fact]
    public async Task RecoveryLoginAsync_NormalizesTypingVariants()
    {
        var codes = await _factory.WithServiceAsync<IAuthRecoveryService, RecoveryCodesResponseDto>(
            (recovery, _) => recovery.GenerateAsync(SeededUserId));
        var typed = codes.Codes[2].ToLowerInvariant().Replace("-", " ");

        var result = await _factory.WithServiceAsync<IAuthRecoveryService, PasskeyCompleteResponseDto?>(
            (recovery, _) => recovery.RecoveryLoginAsync(typed));

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("----")]
    [InlineData("ZZZZ-ZZZZ-ZZZZ")]
    public async Task RecoveryLoginAsync_UnusableInput_IsRejectedWithoutDistinction(string? code)
    {
        var result = await _factory.WithServiceAsync<IAuthRecoveryService, PasskeyCompleteResponseDto?>(
            (recovery, _) => recovery.RecoveryLoginAsync(code));

        Assert.Null(result);
    }
}

public class AuthAccountServiceCredentialTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthAccountServiceCredentialTests(CustomWebApplicationFactory factory) => _factory = factory;

    private async Task<int> SeedCredentialAsync(bool approved, string? label = null)
    {
        return await _factory.WithDbAsync(async db =>
        {
            var entity = new PasskeyCredentialEntity
            {
                AuthUserId = SeededUserId,
                CredentialId = Guid.NewGuid().ToByteArray(),
                PublicKey = [1, 2, 3],
                SignCount = 0,
                CreatedAt = DateTime.UtcNow,
                ApprovedAt = approved ? DateTime.UtcNow : null,
                DeviceLabel = label,
            };
            db.PasskeyCredentials.Add(entity);
            await db.SaveChangesAsync();
            return entity.Id;
        });
    }

    [Fact]
    public async Task ApproveCredentialAsync_IsIdempotentAndScopedToTheOwner()
    {
        var id = await SeedCredentialAsync(approved: false);

        var first = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.ApproveCredentialAsync(SeededUserId, id));
        var again = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.ApproveCredentialAsync(SeededUserId, id));
        var foreign = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.ApproveCredentialAsync(SeededUserId + 1, id));

        Assert.Equal(ServiceOutcome.Success, first.Outcome);
        Assert.Equal(ServiceOutcome.Success, again.Outcome); // already approved, no error
        Assert.Equal(ServiceOutcome.NotFound, foreign.Outcome);
        Assert.NotNull(await _factory.WithDbAsync(db => db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.ApprovedAt).FirstAsync()));
    }

    [Fact]
    public async Task RenameCredentialAsync_BlankLabelClearsIt_OverlongLabelIsInvalid()
    {
        var id = await SeedCredentialAsync(approved: true, label: "Altes Handy");

        var cleared = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.RenameCredentialAsync(SeededUserId, id, "   "));
        Assert.Equal(ServiceOutcome.Success, cleared.Outcome);
        Assert.Null(await _factory.WithDbAsync(db => db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.DeviceLabel).FirstAsync()));

        var tooLong = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.RenameCredentialAsync(SeededUserId, id, new string('x', 101)));
        Assert.Equal(ServiceOutcome.Invalid, tooLong.Outcome);
        Assert.Equal("Label must be at most 100 characters long.", tooLong.Error);
    }

    [Fact]
    public async Task ListCredentialsAsync_ReportsPendingForAnUnapprovedPasskey()
    {
        var pendingId = await SeedCredentialAsync(approved: false);
        var approvedId = await SeedCredentialAsync(approved: true);

        var listed = await _factory.WithServiceAsync<IAuthAccountService, List<PasskeyListItemDto>>(
            (account, _) => account.ListCredentialsAsync(SeededUserId));

        Assert.True(Assert.Single(listed, c => c.Id == pendingId).Pending);
        Assert.False(Assert.Single(listed, c => c.Id == approvedId).Pending);
    }
}

public class AuthAccountServiceLastPasskeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthAccountServiceLastPasskeyTests(CustomWebApplicationFactory factory) => _factory = factory;

    private async Task<int> SeedCredentialAsync(bool approved)
    {
        return await _factory.WithDbAsync(async db =>
        {
            var entity = new PasskeyCredentialEntity
            {
                AuthUserId = SeededUserId,
                CredentialId = Guid.NewGuid().ToByteArray(),
                PublicKey = [1, 2, 3],
                SignCount = 0,
                CreatedAt = DateTime.UtcNow,
                ApprovedAt = approved ? DateTime.UtcNow : null,
            };
            db.PasskeyCredentials.Add(entity);
            await db.SaveChangesAsync();
            return entity.Id;
        });
    }

    [Fact]
    public async Task DeleteCredentialAsync_LastApprovedPasskey_IsRefused_PendingOneIsNot()
    {
        var onlyApproved = await SeedCredentialAsync(approved: true);
        var stillPending = await SeedCredentialAsync(approved: false);

        // A pending additional passkey is useless for login and must never block the account's
        // only real access method from being counted.
        var pendingDelete = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.DeleteCredentialAsync(SeededUserId, stillPending, currentSessionId: 0));
        Assert.Equal(ServiceOutcome.Success, pendingDelete.Outcome);

        var lastDelete = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.DeleteCredentialAsync(SeededUserId, onlyApproved, currentSessionId: 0));
        Assert.Equal(ServiceOutcome.Invalid, lastDelete.Outcome);
        Assert.Equal("The last passkey of an account cannot be removed.", lastDelete.Error);
        Assert.True(await _factory.WithDbAsync(db => db.PasskeyCredentials.AsNoTracking().AnyAsync(c => c.Id == onlyApproved)));
    }
}

public class AuthAccountServiceSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthAccountServiceSessionTests(CustomWebApplicationFactory factory) => _factory = factory;

    private Task<AuthSessionEntity> IssueSessionAsync() => _factory.WithDbAsync(async db =>
    {
        var token = AuthSessionService.IssueSession(db, SeededUserId, DateTime.UtcNow);
        await db.SaveChangesAsync();
        var hash = AuthSessionService.HashToken(token);
        return await db.AuthSessions.AsNoTracking().FirstAsync(s => s.TokenHash == hash);
    });

    [Fact]
    public async Task RevokeOtherSessionsAsync_KeepsExactlyTheCallersOwnSession()
    {
        var mine = await IssueSessionAsync();
        await IssueSessionAsync();
        await IssueSessionAsync();

        var deleted = await _factory.WithServiceAsync<IAuthAccountService, int>(
            (account, _) => account.RevokeOtherSessionsAsync(SeededUserId, mine.Id));

        Assert.Equal(2, deleted);
        var remaining = await _factory.WithDbAsync(db => db.AuthSessions.AsNoTracking()
            .Where(s => s.AuthUserId == SeededUserId).ToListAsync());
        Assert.Equal(mine.Id, Assert.Single(remaining).Id);
    }

    [Fact]
    public async Task LogoutAsync_DeletesOnlyThatSessionRow()
    {
        var mine = await IssueSessionAsync();
        var other = await IssueSessionAsync();

        await _factory.WithServiceAsync<IAuthAccountService>((account, _) => account.LogoutAsync(mine.Id, mine.TokenHash));

        var remaining = await _factory.WithDbAsync(db => db.AuthSessions.AsNoTracking()
            .Where(s => s.AuthUserId == SeededUserId).Select(s => s.Id).ToListAsync());
        Assert.DoesNotContain(mine.Id, remaining);
        Assert.Contains(other.Id, remaining);
    }
}

public class AuthAccountServiceClientKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public AuthAccountServiceClientKeyTests(CustomWebApplicationFactory factory) => _factory = factory;

    private Task<int> SeedClientKeyAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        var entity = new ClientApiKeyEntity
        {
            AuthUserId = SeededUserId,
            ClientId = clientId,
            KeyHash = AuthSessionService.HashToken(AuthSessionService.GenerateToken()),
            GrantedScopes = "",
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientApiKeys.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    });

    [Fact]
    public async Task ListClientKeysAsync_StillListsAKeyWhoseRegistrationIsGone()
    {
        var id = await SeedClientKeyAsync("vanished-addon");

        var listed = await _factory.WithServiceAsync<IAuthAccountService, List<ClientApiKeyListItemDto>>(
            (account, _) => account.ListClientKeysAsync(SeededUserId));

        // ClientName null rather than the row disappearing - otherwise the key would be
        // unrevocable from the UI.
        var row = Assert.Single(listed, k => k.Id == id);
        Assert.Null(row.ClientName);
        Assert.Equal("vanished-addon", row.ClientId);
    }

    [Fact]
    public async Task RevokeClientKeyAsync_AnotherUsersKey_IsNotFound()
    {
        var id = await SeedClientKeyAsync("someone-elses");

        // ClientApiKeys has no EF query filter - the explicit AuthUserId predicate in the
        // service is the only thing keeping one user out of another's keys.
        var foreign = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.RevokeClientKeyAsync(SeededUserId + 1, id));
        Assert.Equal(ServiceOutcome.NotFound, foreign.Outcome);

        var own = await _factory.WithServiceAsync<IAuthAccountService, ServiceResult>(
            (account, _) => account.RevokeClientKeyAsync(SeededUserId, id));
        Assert.Equal(ServiceOutcome.Success, own.Outcome);
    }
}

public class AuthAccountServiceDemoSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthAccountServiceDemoSessionTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task IssueDemoSessionAsync_IssuesARealSessionAndPrunesExpiredRows()
    {
        var expiredHash = await _factory.WithDbAsync(async db =>
        {
            var stale = new AuthSessionEntity
            {
                AuthUserId = 1,
                TokenHash = AuthSessionService.HashToken(AuthSessionService.GenerateToken()),
                IssuedAt = DateTime.UtcNow.AddDays(-400),
                LastUsedAt = DateTime.UtcNow.AddDays(-400),
                ExpiresAt = DateTime.UtcNow.AddDays(-1),
                HardExpiresAt = DateTime.UtcNow.AddDays(-1),
            };
            db.AuthSessions.Add(stale);
            await db.SaveChangesAsync();
            return stale.TokenHash;
        });

        var result = await _factory.WithServiceAsync<IAuthAccountService, PasskeyCompleteResponseDto?>(
            (account, _) => account.IssueDemoSessionAsync());

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Token));
        var hashes = await _factory.WithDbAsync(db => db.AuthSessions.AsNoTracking().Select(s => s.TokenHash).ToListAsync());
        Assert.Contains(AuthSessionService.HashToken(result.Token!), hashes);
        Assert.DoesNotContain(expiredHash, hashes); // opportunistic cleanup, same as LoginComplete
    }
}
