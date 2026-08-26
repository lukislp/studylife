using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // Backoff formula from the identity contract spec (audit A7): retry when
    // now - LastAttemptAt > min(2^Attempts * 30s, 1h) - doubles after each failed attempt,
    // capped so a long studylife-ai outage doesn't stretch retries out indefinitely.
    private static readonly TimeSpan AiKeyOutboxBaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AiKeyOutboxMaxBackoff = TimeSpan.FromHours(1);

    // After this many failed attempts, stop retrying and just keep the row as evidence an
    // operator can act on - a key registration failure is rarer and more consequential to lose
    // silently than e.g. a capture enrichment miss (BackgroundTaskService.CaptureEnrichment.cs's
    // much lower MaxEnrichmentAttempts), hence the far higher ceiling here.
    private const int AiKeyOutboxMaxAttempts = 100;

    /// <summary>
    /// Drains the AI key outbox (audit A7: RegisterKeyAsync/RevokeKeyAsync used to be pure
    /// fire-and-forget, so a studylife-ai outage at generation time lost the plaintext forever
    /// and left the two databases disagreeing) EVERY tick, unconditionally - unlike the
    /// hourly-gated maintenance tasks in ExecuteAsync, a stuck key registration should get
    /// retried promptly once studylife-ai comes back, not up to an hour late.
    ///
    /// Runs OUTSIDE the per-user loop (see ExecuteAsync) and queries table-wide: the outbox
    /// deliberately has NO query filter (see AiKeyOutboxEntity's doc comment) because the
    /// per-user CreatedAt ordering below has to be enforced across the whole table in one pass,
    /// not per already-scoped user.
    ///
    /// Ordering: rows for the SAME user are processed strictly oldest-first, and a delivery
    /// failure stops that user's remaining rows for THIS tick only (not other users, and not a
    /// row that already gave up) - a "register" enqueued after a "revoke" must never be
    /// delivered while the revoke is still stuck, or studylife-ai would end up with a key the
    /// user meant to revoke.
    /// </summary>
    internal async Task RunAiKeyOutboxAsync(StudyLifeDb db)
    {
        if (_aiProxyClient is null) return;

        var now = DateTime.UtcNow;
        var pending = await db.AiKeyOutbox.OrderBy(o => o.AuthUserId).ThenBy(o => o.CreatedAt).ToListAsync();
        if (pending.Count == 0) return;

        foreach (var group in pending.GroupBy(o => o.AuthUserId))
        {
            foreach (var row in group) // already CreatedAt-ordered by the query above
            {
                // Gave up earlier - evidence only, doesn't block later rows of the same user
                // forever (unlike an un-given-up failure below, which does).
                if (row.Attempts >= AiKeyOutboxMaxAttempts) continue;

                if (row.LastAttemptAt is DateTime lastAttempt)
                {
                    // Clamp in double-seconds space BEFORE building a TimeSpan: Math.Pow(2, Attempts)
                    // for a row well past the give-up threshold is astronomically large and would
                    // overflow TimeSpan's tick range if multiplied directly (seen live at Attempts=99).
                    var backoffSeconds = Math.Min(
                        AiKeyOutboxBaseBackoff.TotalSeconds * Math.Pow(2, row.Attempts),
                        AiKeyOutboxMaxBackoff.TotalSeconds);
                    // Not due yet - later rows of this user must wait too (ordering), try again next tick.
                    if (now - lastAttempt <= TimeSpan.FromSeconds(backoffSeconds)) break;
                }

                row.Attempts++;
                row.LastAttemptAt = now;

                var delivered = row.Action == AiKeyOutboxEntity.ActionRegister
                    ? await _aiProxyClient.RegisterKeyAsync(row.AuthUserId, row.AiApiKeyPlaintext ?? "", CancellationToken.None)
                    : await _aiProxyClient.RevokeKeyAsync(row.AuthUserId, CancellationToken.None);

                if (delivered)
                {
                    db.AiKeyOutbox.Remove(row);
                    await db.SaveChangesAsync();
                    continue; // keep going - preserves order for this user's remaining rows
                }

                if (row.Attempts >= AiKeyOutboxMaxAttempts)
                    _logger.LogError(
                        "AI key outbox row {Id} (user {AuthUserId}, action {Action}) gave up after {Attempts} attempts",
                        row.Id, row.AuthUserId, row.Action, row.Attempts);
                await db.SaveChangesAsync(); // persist the failed attempt/backoff before stopping
                break; // stop this user's queue for this tick only - preserves order
            }
        }
    }
}
