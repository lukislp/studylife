// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Which add-on an <see cref="AiKeyOutboxEntity"/> row is for and how it is delivered - shared by
/// the immediate attempt after an account deletion and the background drain, so the two cannot
/// disagree. The AI actions are the original outbox (audit A7); the other two were added for
/// account deletion.
/// </summary>
internal static class AddonOutbox
{
    internal enum Target { Ai, Webhooks, Developers }

    internal static Target TargetOf(string action) => action switch
    {
        AiKeyOutboxEntity.ActionPurgeWebhooks => Target.Webhooks,
        AiKeyOutboxEntity.ActionRevokeDeveloper => Target.Developers,
        _ => Target.Ai,
    };

    /// <summary>True = delivered, false = the add-on did not confirm, null = no client for that
    /// add-on exists in this process (leave the row alone).</summary>
    internal static async Task<bool?> TryDeliverAsync(
        AiKeyOutboxEntity row, AiProxyClient? ai, WebhooksProxyClient? webhooks, DeveloperProxyClient? developers, CancellationToken ct)
    {
        switch (TargetOf(row.Action))
        {
            case Target.Webhooks:
                return webhooks is null ? null : await webhooks.PurgeUserAsync(row.AuthUserId, ct);
            case Target.Developers:
                return developers is null ? null : await developers.RevokeKeyAsync(row.AuthUserId, ct);
            default:
                if (ai is null) return null;
                return row.Action == AiKeyOutboxEntity.ActionRegister
                    ? await ai.RegisterKeyAsync(row.AuthUserId, row.AiApiKeyPlaintext ?? "", ct)
                    : await ai.RevokeKeyAsync(row.AuthUserId, ct);
        }
    }
}
