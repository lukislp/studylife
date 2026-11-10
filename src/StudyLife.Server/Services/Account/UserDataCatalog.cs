// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// THE authoritative answer to "which stored data belongs to a user", used by
/// <see cref="AccountDeletionService"/> and pinned by <c>UserDataCatalogTests</c>.
///
/// It is derived from the EF model rather than copied from <c>DemoSeeder</c> or the JSON import
/// (both wipe lists predate deletion and cover different questions): every entity that carries an
/// <c>AuthUserId</c> column is "per-user rows" and is removed generically, so a table added next
/// month is covered the moment it has that column. What cannot be derived is listed by hand below,
/// each entry with the reason it needs its own treatment. The guard test fails for any entity that
/// is neither generic, nor listed here, nor declared instance-wide - and for any other
/// <c>*UserId</c> column that nobody has decided about - so "forgot to extend deletion" cannot pass
/// review silently.
/// </summary>
public static class UserDataCatalog
{
    /// <summary>The column every per-user table uses; the generic rule keys on it.</summary>
    public const string UserColumn = "AuthUserId";

    /// <summary>Entities with their own deletion step. They may carry <see cref="UserColumn"/> too
    /// (the outbox does), which is exactly why they cannot ride the generic rule.</summary>
    public static readonly IReadOnlyDictionary<Type, string> Explicit = new Dictionary<Type, string>
    {
        [typeof(AuthUserEntity)] =
            "The account row (display name, hashes of all eight API-key slots, calendar token, owner flag), deleted last by Id.",
        [typeof(AuthInviteEntity)] =
            "CreatedByUserId: the user's invites are deleted. UsedByUserId: invites the user consumed are kept as spent " +
            "(UsedAt stays, so they can never be redeemed again) with the reference set to null - no foreign key forces " +
            "the row to stay, but an owner's invite list should not lose its history just because a guest left.",
        [typeof(OAuthClientEntity)] =
            "OwnerAuthUserId: apps the user registered as a developer are deleted together with every ClientApiKey issued " +
            "for them to anyone. Other users' integrations with such an app stop working; the deletion dialog says so.",
        [typeof(AiKeyOutboxEntity)] =
            "Pending 'register' rows (they may hold a plaintext AI key) are deleted. 'revoke'/'purge' rows must SURVIVE the " +
            "user: they are the retry queue for the add-ons and hold nothing but the user id and an action name.",
    };

    /// <summary>Further user-reference columns that exist on explicit entities and are handled in
    /// their steps above. Together with <see cref="UserColumn"/> this is the full set of columns the
    /// guard accepts as "decided".</summary>
    public static readonly IReadOnlySet<(Type Entity, string Property)> ExplicitColumns = new HashSet<(Type, string)>
    {
        (typeof(AuthInviteEntity), nameof(AuthInviteEntity.CreatedByUserId)),
        (typeof(AuthInviteEntity), nameof(AuthInviteEntity.UsedByUserId)),
        (typeof(OAuthClientEntity), nameof(OAuthClientEntity.OwnerAuthUserId)),
        (typeof(AiKeyOutboxEntity), UserColumn),
    };

    /// <summary>Tables that hold no user data at all: installation-wide configuration.</summary>
    public static readonly IReadOnlyDictionary<Type, string> InstanceWide = new Dictionary<Type, string>
    {
        [typeof(SystemSecretsEntity)] = "VAPID key pair and the setup code of the installation.",
        [typeof(InstanceInfoEntity)] = "Random id of this installation.",
    };

    /// <summary>Entity types removed by the generic rule: everything with an int
    /// <see cref="UserColumn"/> that has no explicit step.</summary>
    public static IReadOnlyList<IEntityType> PerUserTables(IModel model) =>
        model.GetEntityTypes()
            .Where(t => !Explicit.ContainsKey(t.ClrType) && HasUserColumn(t))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Entities the catalog has no decision for - must be empty (see the guard test).</summary>
    public static IReadOnlyList<Type> Unclassified(IModel model) =>
        model.GetEntityTypes()
            .Where(t => !Explicit.ContainsKey(t.ClrType) && !InstanceWide.ContainsKey(t.ClrType) && !HasUserColumn(t))
            .Select(t => t.ClrType)
            .ToList();

    /// <summary>Columns that look like a user reference (<c>*UserId</c>) but are neither
    /// <see cref="UserColumn"/> on a generic table nor listed in <see cref="ExplicitColumns"/> -
    /// must be empty (see the guard test).</summary>
    public static IReadOnlyList<(Type Entity, string Property)> UndecidedUserColumns(IModel model) =>
        model.GetEntityTypes()
            .Where(t => !InstanceWide.ContainsKey(t.ClrType))
            .SelectMany(t => t.GetProperties()
                .Where(p => p.Name.EndsWith("UserId", StringComparison.Ordinal))
                .Where(p => !ExplicitColumns.Contains((t.ClrType, p.Name)))
                .Where(p => !(p.Name == UserColumn && !Explicit.ContainsKey(t.ClrType)))
                .Where(p => !(t.ClrType == typeof(AuthUserEntity)))
                .Select(p => (t.ClrType, p.Name)))
            .ToList();

    private static bool HasUserColumn(IEntityType type) =>
        type.FindProperty(UserColumn) is { } p && p.ClrType == typeof(int);
}
