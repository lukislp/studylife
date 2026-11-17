// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>Where the server finds the hashes of the API keys it issued to an add-on.</summary>
public enum AddonPurgeKeySource
{
    /// <summary>The fixed MCP slot, <see cref="AuthUserEntity.McpApiKeyHash"/>.</summary>
    McpApiKey,

    /// <summary><see cref="ClientApiKeyEntity"/> rows whose ClientId is the add-on's client id.</summary>
    ClientApiKeys,
}

/// <summary>
/// The add-ons that receive the account-deletion purge call
/// (<c>POST /internal/users/{id}/purge</c>, docs/ARCHITECTURE.md "Account deletion"): the ones that
/// keep per-user data of their own and have no other way to learn that an account is gone. Adding
/// an add-on means one entry here; configuration section, outbox action and key lookup follow
/// from it.
/// </summary>
public sealed record AddonPurgeTarget(
    string Name, string OutboxAction, AddonPurgeKeySource KeySource, string? DefaultClientId = null)
{
    public static readonly AddonPurgeTarget Mcp =
        new("Mcp", AiKeyOutboxEntity.ActionPurgeMcp, AddonPurgeKeySource.McpApiKey);

    public static readonly AddonPurgeTarget Alexa =
        new("Alexa", AiKeyOutboxEntity.ActionPurgeAlexa, AddonPurgeKeySource.ClientApiKeys, "studylife-alexa");

    public static readonly AddonPurgeTarget Telegram =
        new("Telegram", AiKeyOutboxEntity.ActionPurgeTelegram, AddonPurgeKeySource.ClientApiKeys, "studylife-telegram");

    public static readonly IReadOnlyList<AddonPurgeTarget> All = [Mcp, Alexa, Telegram];

    /// <summary>The configuration section, e.g. <c>AddonPurge:Mcp</c>; also the options name.</summary>
    public string SectionPath => $"{Configuration.AddonPurgeOptions.SectionName}:{Name}";

    public static AddonPurgeTarget? ForAction(string action) => All.FirstOrDefault(t => t.OutboxAction == action);

    public static AddonPurgeTarget? ForName(string? name) => All.FirstOrDefault(t => t.Name == name);
}
