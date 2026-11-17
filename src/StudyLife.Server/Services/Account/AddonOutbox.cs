// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Which add-on an <see cref="AiKeyOutboxEntity"/> row is for and the retry policy - shared by
/// the immediate attempt after an account deletion and the background drain, so the two cannot
/// disagree. The AI actions are the original outbox (audit A7); the others were added for
/// account deletion.
/// </summary>
internal static partial class AddonOutbox
{
    internal enum Target { Ai, Webhooks, Developers, Mcp, Alexa, Telegram }

    /// <summary>After this many failed attempts a row that is never given up is reported with a
    /// warning, and from then on at most once per <see cref="WarnInterval"/> (audit F-15).</summary>
    internal const int WarnAfterAttempts = 10;

    internal static readonly TimeSpan WarnInterval = TimeSpan.FromDays(1);

    /// <summary>The contract's limit of hashes per purge call; more keys = more rows.</summary>
    internal const int MaxKeyHashesPerRow = 50;

    internal static Target TargetOf(string action) => action switch
    {
        AiKeyOutboxEntity.ActionPurgeWebhooks => Target.Webhooks,
        AiKeyOutboxEntity.ActionRevokeDeveloper => Target.Developers,
        AiKeyOutboxEntity.ActionPurgeMcp => Target.Mcp,
        AiKeyOutboxEntity.ActionPurgeAlexa => Target.Alexa,
        AiKeyOutboxEntity.ActionPurgeTelegram => Target.Telegram,
        _ => Target.Ai,
    };

    /// <summary>
    /// Only the AI key registration may be given up (after the drain's attempt limit): it carries
    /// a plaintext key that should not sit in the table forever, and a lost registration only
    /// means the user generates a new key. Every revoke and purge is retried for as long as it
    /// takes - dropping one would leave a deleted user's data in an add-on (audit F-15).
    /// </summary>
    internal static bool MayGiveUp(string action) => action == AiKeyOutboxEntity.ActionRegister;

    /// <summary>True when this failed attempt of a never-given-up row should be logged as a
    /// warning: from <see cref="WarnAfterAttempts"/> on, once per <see cref="WarnInterval"/>.</summary>
    internal static bool ShouldWarn(AiKeyOutboxEntity row, DateTime now) =>
        !MayGiveUp(row.Action)
        && row.Attempts >= WarnAfterAttempts
        && (row.FailureWarnedAt is not { } warned || now - warned >= WarnInterval);

    /// <summary>Hashes as stored in <see cref="AiKeyOutboxEntity.KeyHashes"/>.</summary>
    internal static string? FormatKeyHashes(IEnumerable<string> hashes)
    {
        var joined = string.Join(',', hashes);
        return joined.Length == 0 ? null : joined;
    }

    internal static IReadOnlyList<string> ParseKeyHashes(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>What the add-ons accept (64 lowercase hex characters, the format of
    /// AuthSessionService.HashToken). Anything else would make the add-on reject the whole call
    /// with 422 - and a purge is retried forever - so such a value is never queued.</summary>
    internal static bool IsKeyHash(string value) => KeyHashPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyHashPattern();
}

/// <summary>
/// The add-on clients one process has, bundled so the two delivery paths (the immediate attempt
/// in <see cref="AccountDeletionService"/> and BackgroundTaskService.RunAiKeyOutboxAsync) take one
/// argument and a new add-on does not grow their signatures. A null client means this process
/// cannot deliver that add-on's rows; they are left for one that can.
/// </summary>
internal sealed class AddonOutboxDelivery(
    AiProxyClient? ai, WebhooksProxyClient? webhooks, DeveloperProxyClient? developers, AddonPurgeClients? purges)
{
    public bool HasAnyClient => ai is not null || webhooks is not null || developers is not null || purges is not null;

    /// <summary>True = delivered, false = the add-on did not confirm, null = no client for that
    /// add-on exists in this process (leave the row alone, it is not an attempt).</summary>
    public async Task<bool?> TryDeliverAsync(AiKeyOutboxEntity row, CancellationToken ct)
    {
        if (AddonPurgeTarget.ForAction(row.Action) is { } purgeTarget)
        {
            return purges?.For(purgeTarget) is { } client
                ? await client.PurgeUserAsync(row.AuthUserId, AddonOutbox.ParseKeyHashes(row.KeyHashes), ct)
                : null;
        }

        switch (AddonOutbox.TargetOf(row.Action))
        {
            case AddonOutbox.Target.Webhooks:
                return webhooks is null ? null : await webhooks.PurgeUserAsync(row.AuthUserId, ct);
            case AddonOutbox.Target.Developers:
                return developers is null ? null : await developers.RevokeKeyAsync(row.AuthUserId, ct);
            default:
                if (ai is null) return null;
                return row.Action == AiKeyOutboxEntity.ActionRegister
                    ? await ai.RegisterKeyAsync(row.AuthUserId, row.AiApiKeyPlaintext ?? "", ct)
                    : await ai.RevokeKeyAsync(row.AuthUserId, ct);
        }
    }
}
