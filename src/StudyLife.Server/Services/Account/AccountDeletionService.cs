// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.Account;

/// <summary>What a deletion request ended in. Controllers map this to status codes; nothing here
/// knows about HTTP.</summary>
public enum AccountDeletionOutcome
{
    Deleted,
    /// <summary>No such account.</summary>
    NotFound,
    /// <summary>Public demo instance: nothing may be deleted (the write block already refuses the
    /// request first - this is the second line of defence for non-HTTP callers).</summary>
    DemoMode,
    /// <summary>Somebody else's account was addressed by a user who is not the owner.</summary>
    InitiatorNotOwner,
    /// <summary>The owner administers the installation and cannot leave while other accounts exist.</summary>
    OwnerHasOtherUsers,
}

public sealed record AccountDeletionResult(AccountDeletionOutcome Outcome, AccountDeletionResultDto? Value = null);

/// <summary>
/// THE account-deletion use case (docs/ARCHITECTURE.md "Account deletion"): policy, add-on
/// clean-up, removal of everything the user owns, session invalidation and the first-run reset.
/// Reached from two places that must behave identically - a user deleting their own account and
/// the owner removing another user - so neither controller carries any of this logic.
///
/// <b>Policy.</b> The owner cannot be deleted while other accounts exist (they administer the
/// installation; there is deliberately no ownership transfer). The owner as the LAST account can
/// delete themselves, which returns the installation to first run. Any other user removes only
/// themselves; only the owner may remove somebody else.
///
/// <b>Order and failure behaviour.</b> One database transaction removes every local row and, in the
/// same transaction, queues the add-on clean-ups (studylife-ai revoke, studylife-webhooks purge,
/// studylife-developers revoke) in the existing outbox table. So either everything local is gone
/// AND the retries are durably queued, or nothing changed - there is no half-deleted account and
/// no way to lose the add-on work. Delivery is attempted immediately after the commit; whatever
/// fails stays in the outbox and BackgroundTaskService retries it with backoff (it holds only the
/// user id and an action name, no personal data, which is why it may outlive the account). An
/// add-on outage therefore never blocks a deletion, and the response says how many clean-ups are
/// still pending. The alternative (add-ons first, then local data) would let an unreachable add-on
/// stop the user from deleting their account at all.
///
/// <b>What belongs to a user</b> is decided by <see cref="UserDataCatalog"/>, not here.
/// </summary>
public interface IAccountDeletionService
{
    /// <summary>What the "Delete account" card shows before the user commits.</summary>
    Task<AccountDeletionPreviewDto> PreviewAsync(int userId);

    /// <summary>The policy part alone, so a caller can refuse BEFORE it spends a one-time proof
    /// (a recovery code) on a request that would be rejected anyway. <see cref="DeleteAsync"/>
    /// re-checks it inside the use case.</summary>
    Task<AccountDeletionOutcome> CheckPolicyAsync(int userId, int initiatedByUserId);

    Task<AccountDeletionResult> DeleteAsync(int userId, int initiatedByUserId, CancellationToken ct);

    /// <summary>Every account except the owner's own - the list behind the owner's "remove user" action.</summary>
    Task<List<UserListItemDto>> ListOtherUsersAsync(int ownerUserId);
}

public sealed class AccountDeletionService(
    StudyLifeDb db,
    IOwnershipService ownership,
    IConfiguration config,
    AuthSessionCache sessionCache,
    SystemSecretsService systemSecrets,
    AiProxyClient aiProxy,
    WebhooksProxyClient webhooks,
    DeveloperProxyClient developers,
    SettingsCacheVersion settingsVersion,
    SessionHistoryCacheVersion sessionsVersion,
    ILogger<AccountDeletionService> logger) : IAccountDeletionService
{
    public async Task<AccountDeletionPreviewDto> PreviewAsync(int userId)
    {
        var others = await db.AuthUsers.AsNoTracking().CountAsync(u => u.Id != userId);
        var isOwner = await ownership.IsOwnerAsync(userId);
        var preview = new AccountDeletionPreviewDto
        {
            IsOwner = isOwner,
            OtherUserCount = others,
            OwnedClientCount = await db.OAuthClients.AsNoTracking().CountAsync(c => c.OwnerAuthUserId == userId),
            InstallationWillReset = others == 0,
            Available = true,
        };
        if (DemoModeGuard.IsEnabled(config))
        {
            preview.Available = false;
            preview.UnavailableReason = "demo";
        }
        else if (isOwner && others > 0)
        {
            preview.Available = false;
            preview.UnavailableReason = "owner-has-other-users";
        }
        return preview;
    }

    public async Task<AccountDeletionOutcome> CheckPolicyAsync(int userId, int initiatedByUserId)
    {
        if (DemoModeGuard.IsEnabled(config)) return AccountDeletionOutcome.DemoMode;
        if (!await db.AuthUsers.AsNoTracking().AnyAsync(u => u.Id == userId)) return AccountDeletionOutcome.NotFound;

        if (initiatedByUserId != userId && !await ownership.IsOwnerAsync(initiatedByUserId))
            return AccountDeletionOutcome.InitiatorNotOwner;

        if (await db.AuthUsers.AsNoTracking().AnyAsync(u => u.Id != userId)
            && await ownership.IsOwnerAsync(userId))
            return AccountDeletionOutcome.OwnerHasOtherUsers;

        return AccountDeletionOutcome.Deleted;
    }

    public async Task<List<UserListItemDto>> ListOtherUsersAsync(int ownerUserId) =>
        await db.AuthUsers.AsNoTracking()
            .Where(u => u.Id != ownerUserId)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Select(u => new UserListItemDto { Id = u.Id, DisplayName = u.DisplayName, CreatedAt = u.CreatedAt })
            .ToListAsync();

    public async Task<AccountDeletionResult> DeleteAsync(int userId, int initiatedByUserId, CancellationToken ct)
    {
        var policy = await CheckPolicyAsync(userId, initiatedByUserId);
        if (policy != AccountDeletionOutcome.Deleted) return new AccountDeletionResult(policy);

        var user = await db.AuthUsers.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var tokenHashes = await db.AuthSessions.AsNoTracking()
            .Where(s => s.AuthUserId == userId).Select(s => s.TokenHash).ToListAsync(ct);

        var queued = new List<AiKeyOutboxEntity>();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // 1. Add-on clean-up intents first, inside the same transaction (see the class doc).
            //    A pending 'register' row may hold a plaintext AI key - it goes before anything is queued.
            var hadPendingAiRegistration = await db.AiKeyOutbox
                .AnyAsync(o => o.AuthUserId == userId && o.Action == AiKeyOutboxEntity.ActionRegister, ct);
            await db.AiKeyOutbox
                .Where(o => o.AuthUserId == userId && o.Action == AiKeyOutboxEntity.ActionRegister)
                .ExecuteDeleteAsync(ct);

            var now = DateTime.UtcNow;
            if (aiProxy.Enabled && (user.AiApiKeyHash is not null || hadPendingAiRegistration))
                queued.Add(NewOutboxRow(userId, AiKeyOutboxEntity.ActionRevoke, now));
            if (webhooks.Enabled)
                queued.Add(NewOutboxRow(userId, AiKeyOutboxEntity.ActionPurgeWebhooks, now));
            if (developers.Enabled && user.DeveloperApiKeyHash is not null)
                queued.Add(NewOutboxRow(userId, AiKeyOutboxEntity.ActionRevokeDeveloper, now));
            db.AiKeyOutbox.AddRange(queued);
            await db.SaveChangesAsync(ct);

            // 2. Everything with an AuthUserId column, generically (UserDataCatalog.PerUserTables).
            foreach (var table in UserDataCatalog.PerUserTables(db.Model))
                await DeleteRowsAsync(table.ClrType, UserDataCatalog.UserColumn, userId, ct);

            // 3. The explicit steps (UserDataCatalog.Explicit).
            await db.AuthInvites.Where(i => i.CreatedByUserId == userId).ExecuteDeleteAsync(ct);
            await db.AuthInvites.Where(i => i.UsedByUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedByUserId, (int?)null), ct);
            var ownedClientIds = await db.OAuthClients.Where(c => c.OwnerAuthUserId == userId)
                .Select(c => c.ClientId).ToListAsync(ct);
            if (ownedClientIds.Count > 0)
            {
                await db.ClientApiKeys.Where(k => ownedClientIds.Contains(k.ClientId)).ExecuteDeleteAsync(ct);
                await db.OAuthClients.Where(c => c.OwnerAuthUserId == userId).ExecuteDeleteAsync(ct);
            }

            // 4. The account row itself, last.
            await db.AuthUsers.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

            // 5. Last account gone: leave nothing that could still look like an installation in use
            //    (a stray credential would make login/begin answer instead of the first-run flow).
            if (!await db.AuthUsers.AnyAsync(ct))
            {
                await db.AuthSessions.ExecuteDeleteAsync(ct);
                await db.PasskeyCredentials.ExecuteDeleteAsync(ct);
                await db.RecoveryCodes.ExecuteDeleteAsync(ct);
                await db.AuthInvites.ExecuteDeleteAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }

        // From here on the account is gone; everything below is best effort and never undoes it.
        foreach (var hash in tokenHashes) sessionCache.Remove(hash);
        await TryBumpAsync(userId);

        var pending = await DeliverQueuedAsync(queued);

        var installationReset = !await db.AuthUsers.AnyAsync(CancellationToken.None);
        if (installationReset) await AnnounceFirstRunAsync();

        // The user id and the outcome only - no name, no content.
        logger.LogInformation(
            "Account {UserId} deleted (initiated by {InitiatedBy}); installation reset: {Reset}; add-on clean-ups still pending: {Pending}",
            userId, initiatedByUserId, installationReset, pending);

        return new AccountDeletionResult(AccountDeletionOutcome.Deleted,
            new AccountDeletionResultDto { InstallationReset = installationReset, AddonCleanupPending = pending });
    }

    private static AiKeyOutboxEntity NewOutboxRow(int userId, string action, DateTime now) =>
        new() { AuthUserId = userId, Action = action, CreatedAt = now };

    private static readonly MethodInfo DeleteRowsMethod =
        typeof(AccountDeletionService).GetMethod(nameof(DeleteRowsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private Task DeleteRowsAsync(Type entityType, string column, int userId, CancellationToken ct) =>
        (Task)DeleteRowsMethod.MakeGenericMethod(entityType).Invoke(null, [db, column, userId, ct])!;

    // IgnoreQueryFilters: this runs for a user other than (or without) the request's current one.
    private static Task<int> DeleteRowsOfAsync<T>(StudyLifeDb db, string column, int userId, CancellationToken ct)
        where T : class =>
        db.Set<T>().IgnoreQueryFilters()
            .Where(e => EF.Property<int>(e, column) == userId)
            .ExecuteDeleteAsync(ct);

    /// <summary>The immediate delivery attempt after the commit; returns how many stay queued.</summary>
    private async Task<int> DeliverQueuedAsync(List<AiKeyOutboxEntity> rows)
    {
        var pending = 0;
        foreach (var row in rows)
        {
            var delivered = await AddonOutbox.TryDeliverAsync(row, aiProxy, webhooks, developers, CancellationToken.None);
            if (delivered == true)
            {
                await db.AiKeyOutbox.Where(o => o.Id == row.Id).ExecuteDeleteAsync(CancellationToken.None);
                continue;
            }
            pending++;
            // Same bookkeeping as the other outbox producers: the fast-path attempt counts, so the
            // drain's backoff starts from here.
            await db.AiKeyOutbox.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Attempts, 1)
                .SetProperty(o => o.LastAttemptAt, DateTime.UtcNow), CancellationToken.None);
        }
        return pending;
    }

    private async Task TryBumpAsync(int userId)
    {
        try
        {
            await settingsVersion.BumpAsync(userId);
            await sessionsVersion.BumpAsync(userId);
        }
        catch (Exception ex)
        {
            // Cached payloads then simply expire on their own TTL; the ids are never reused.
            logger.LogWarning(ex, "Could not invalidate cached data of deleted account {UserId}", userId);
        }
    }

    /// <summary>The installation is empty again: issue a fresh setup code (the previous one was
    /// cleared at the first registration) and print it exactly like startup does, so the operator
    /// finds it in the server log.</summary>
    private async Task AnnounceFirstRunAsync()
    {
        var code = await systemSecrets.EnsureSetupSecretAsync();
        SystemSecretsService.WriteSetupBanner(Console.Out, code,
            "The last account was deleted - this installation is back at first run.");
    }
}
