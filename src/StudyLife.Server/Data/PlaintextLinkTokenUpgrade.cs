// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Services;

namespace StudyLife.Server.Data;

/// <summary>
/// Data step of audit 2026-11-16 L5: the calendar token and the progress-share token used to sit
/// in the database in plaintext. Migration AddLinkTokenHashes adds the hash columns; this step
/// hashes whatever the old plaintext columns still hold into them and clears the plaintext, so
/// every calendar subscription and every shared link handed out before the upgrade keeps working
/// (the presented token is hashed at lookup).
///
/// Runs right after Database.Migrate() in the migrating process (Program.cs). It is a separate C#
/// step, not SQL inside the migration, because SQLite has no SHA-256 function and both providers
/// should go through the one hash implementation the lookups use. Idempotent: only rows whose
/// plaintext column is still set are touched, so a second run finds nothing.
/// </summary>
public static class PlaintextLinkTokenUpgrade
{
    /// <summary>Returns how many tokens were moved (calendar + share).</summary>
    public static async Task<int> RunAsync(StudyLifeDb db, CancellationToken ct = default)
    {
        var users = await db.AuthUsers
            .Where(u => u.LegacyCalendarToken != null)
            .ToListAsync(ct);
        foreach (var user in users)
        {
            // A token from before the upgrade wins over nothing, never over a newer hash: a hash
            // is only written by this release, which clears the plaintext in the same save.
            user.CalendarTokenHash ??= AuthSessionService.HashToken(user.LegacyCalendarToken!);
            user.LegacyCalendarToken = null;
        }

        // IgnoreQueryFilters: this runs at startup without a current user, and the AuthUserId
        // filter would otherwise resolve to user 0 and match nothing.
        var settings = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.LegacyProgressShareToken != null)
            .ToListAsync(ct);
        foreach (var row in settings)
        {
            row.ProgressShareTokenHash ??= AuthSessionService.HashToken(row.LegacyProgressShareToken!);
            row.LegacyProgressShareToken = null;
        }

        if (users.Count + settings.Count > 0)
            await db.SaveChangesAsync(ct);
        return users.Count + settings.Count;
    }
}
