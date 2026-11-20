// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Builds the outbox rows for the account-deletion purge of the add-ons with per-user data
/// (<see cref="AddonPurgeTarget"/>). Runs inside the deletion transaction BEFORE the user's rows
/// are removed, because the add-ons find their data by the hashes of the keys the server issued
/// to them, and those hashes are gone with the user's rows.
/// </summary>
internal static class AddonPurgeQueue
{
    /// <summary>
    /// One row per configured add-on (more if the user holds more than
    /// <see cref="AddonOutbox.MaxKeyHashesPerRow"/> keys for it). Every add-on is always called,
    /// also without a key: they all store the user id as well (the call carries it in the URL), and
    /// a user who revoked the key earlier still has data there that only the user id can reach.
    /// </summary>
    public static async Task<List<AiKeyOutboxEntity>> RowsForAsync(
        StudyLifeDb db, AddonPurgeClients purges, AuthUserEntity user, DateTime now, CancellationToken ct)
    {
        var rows = new List<AiKeyOutboxEntity>();
        foreach (var client in purges.All.Where(c => c.Configured))
        {
            var hashes = (await KeyHashesAsync(db, client, user, ct)).Where(AddonOutbox.IsKeyHash).Distinct().ToList();
            if (hashes.Count == 0)
            {
                rows.Add(Row(user.Id, client.Target, [], now));
                continue;
            }
            rows.AddRange(hashes.Chunk(AddonOutbox.MaxKeyHashesPerRow).Select(chunk => Row(user.Id, client.Target, chunk, now)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<string>> KeyHashesAsync(
        StudyLifeDb db, AddonPurgeClient client, AuthUserEntity user, CancellationToken ct) =>
        client.Target.KeySource switch
        {
            AddonPurgeKeySource.McpApiKey => user.McpApiKeyHash is { } hash ? [hash] : [],
            _ => await db.ClientApiKeys.AsNoTracking()
                .Where(k => k.AuthUserId == user.Id && k.ClientId == client.ClientId)
                .OrderBy(k => k.Id)
                .Select(k => k.KeyHash)
                .ToListAsync(ct),
        };

    private static AiKeyOutboxEntity Row(int userId, AddonPurgeTarget target, IEnumerable<string> hashes, DateTime now) =>
        new() { AuthUserId = userId, Action = target.OutboxAction, KeyHashes = AddonOutbox.FormatKeyHashes(hashes), CreatedAt = now };
}
