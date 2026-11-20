// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Services.Erasure;

/// <summary>What one <see cref="ErasureReconciler.RunAsync"/> pass did.</summary>
public sealed record ErasureReconcileResult(
    int Pruned, int AccountsDeleted, int TelemetryWithdrawn, int AiConsentWithdrawn, int Deferred)
{
    public int Applied => AccountsDeleted + TelemetryWithdrawn + AiConsentWithdrawn;
}

/// <summary>
/// Applies the erasure journal to the database again, right after start-up: an account deleted
/// after the backup that was just restored goes through the normal deletion path
/// (<see cref="IAccountDeletionService"/>, which also queues the add-on purges), and a consent
/// withdrawn after it is withdrawn again (<see cref="IAiConsentService.WithdrawAsync"/> for AI,
/// the telemetry answer plus its log row for telemetry).
///
/// <b>Why it can run on every start.</b> Each entry is applied only when the database still
/// contradicts it, so a pass over a database that never went back in time changes nothing:
/// <list type="bullet">
/// <item>a deletion needs an account with that id AND that creation time - a database that
/// handed the id out again to someone else is left alone;</item>
/// <item>a withdrawal needs the consent to be granted at a time BEFORE the withdrawal - a later
/// re-grant (the user changed their mind) is never undone.</item>
/// </list>
/// Nothing can be wrongly deleted by a stale entry, and an entry that could not be applied (the
/// installation owner is only deletable as the last account) stays for the next start.
/// A later re-grant that exists only in the older backup is not carried over: restoring errs on
/// the side of the withdrawal, the user is asked again.
/// </summary>
public sealed class ErasureReconciler(
    IErasureJournal journal,
    StudyLifeDb db,
    IAccountDeletionService deletion,
    IAiConsentService aiConsent,
    SettingsCacheVersion settingsVersion,
    RetentionOptions retention,
    ILogger<ErasureReconciler> logger)
{
    public async Task<ErasureReconcileResult> RunAsync(DateTime utcNow, CancellationToken ct = default)
    {
        if (!journal.Enabled) return new ErasureReconcileResult(0, 0, 0, 0, 0);

        var pruned = journal.Prune(utcNow.AddDays(-retention.ErasureJournalDays));
        var entries = journal.Read();

        int accounts = 0, telemetry = 0, ai = 0, deferred = 0;
        var deletedIds = new HashSet<int>();

        // Oldest first: the owner can only leave as the last account, which is the order the
        // deletions originally happened in.
        foreach (var entry in entries.Where(e => e.Kind == ErasureKind.AccountDeleted))
        {
            var user = await db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entry.UserId, ct);
            if (user is null || !SameAccount(user, entry)) continue;

            var result = await deletion.DeleteAsync(entry.UserId, entry.UserId, ct);
            if (result.Outcome == AccountDeletionOutcome.Deleted)
            {
                accounts++;
                deletedIds.Add(entry.UserId);
            }
            else
            {
                deferred++;
                logger.LogWarning("Erasure journal: account {UserId} came back with a restore but could not be deleted again ({Outcome}); retried at the next start",
                    entry.UserId, result.Outcome);
            }
        }

        foreach (var entry in entries.Where(e => e.Kind != ErasureKind.AccountDeleted && !deletedIds.Contains(e.UserId)))
        {
            var user = await db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entry.UserId, ct);
            if (user is null || !SameAccount(user, entry)) continue;

            var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.AuthUserId == entry.UserId, ct);
            if (settings is null) continue;

            if (entry.Kind == ErasureKind.TelemetryWithdrawn)
            {
                if (settings.TelemetryConsent != true || settings.TelemetryConsentAt >= entry.At) continue;
                TelemetryConsentRecord.Apply(settings, false, entry.At);
                // The log row the lost withdrawal had: the history stays provable.
                db.TelemetryConsentLog.Add(new TelemetryConsentLogEntity
                {
                    AuthUserId = entry.UserId,
                    Answer = false,
                    AnsweredAt = entry.At,
                    TextVersion = TelemetryConsentRecord.CurrentVersion,
                });
                await db.SaveChangesAsync(ct);
                telemetry++;
            }
            else
            {
                if (settings.AiConsentGrantedAt is not { } granted || granted >= entry.At) continue;
                await aiConsent.WithdrawAsync(entry.UserId, ct);
                ai++;
            }
            await TryBumpAsync(entry.UserId);
        }

        if (pruned + accounts + telemetry + ai + deferred > 0)
            logger.LogInformation(
                "Erasure journal: {Accounts} account(s) deleted again, {Telemetry} telemetry and {Ai} AI withdrawal(s) re-applied, {Deferred} deferred, {Pruned} old entr(ies) pruned",
                accounts, telemetry, ai, deferred, pruned);

        return new ErasureReconcileResult(pruned, accounts, telemetry, ai, deferred);
    }

    // The creation time is compared with a millisecond of slack: Postgres keeps microseconds,
    // SQLite text keeps ticks, and the entry holds whatever the database returned.
    private static bool SameAccount(AuthUserEntity user, ErasureEntry entry) =>
        Math.Abs((user.CreatedAt - entry.UserCreatedAt).TotalMilliseconds) < 1;

    private async Task TryBumpAsync(int userId)
    {
        try { await settingsVersion.BumpAsync(userId); }
        catch (Exception ex) { logger.LogWarning(ex, "Settings cache version not bumped for {UserId}", userId); }
    }
}

/// <summary>Recording side: the three call sites (account deletion, the two withdrawals) share
/// the lookup of the account's creation time here.</summary>
public static class ErasureJournalRecording
{
    public static async Task RecordAsync(this IErasureJournal journal, StudyLifeDb db, ErasureKind kind, int userId, DateTime at)
    {
        if (!journal.Enabled) return;
        var created = await db.AuthUsers.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => (DateTime?)u.CreatedAt).FirstOrDefaultAsync();
        if (created is { } c) journal.Record(new ErasureEntry(kind, userId, c, at));
    }
}
