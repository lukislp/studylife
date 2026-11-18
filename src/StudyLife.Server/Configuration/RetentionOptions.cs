// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;

namespace StudyLife.Server.Configuration;

/// <summary>
/// How long rows that have outlived their purpose stay in the database before the daily
/// <see cref="Services.DataRetentionCleaner"/> removes them (GDPR storage limitation, Art. 5(1)(e)).
/// Env-var form: <c>Retention__InviteDays=30</c>. Every value is in days and must be at least 1.
/// Expired login sessions have no setting: they are useless the moment they expire.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Invites are deleted this long after they were used or after they expired.</summary>
    [Range(1, 3650)]
    public int InviteDays { get; set; } = 30;

    /// <summary>A recovery code is deleted this long after it was spent.</summary>
    [Range(1, 3650)]
    public int UsedRecoveryCodeDays { get; set; } = 30;

    /// <summary>An additional passkey that nobody approved on a signed-in device is deleted
    /// after this long.</summary>
    [Range(1, 3650)]
    public int UnapprovedPasskeyDays { get; set; } = 7;

    /// <summary>A push subscription older than this is deleted when its owner has not used any
    /// session for as long (the subscription itself has no "last used" column; a signed-in or
    /// recently active account keeps its devices).</summary>
    [Range(1, 3650)]
    public int InactivePushSubscriptionDays { get; set; } = 180;
}
