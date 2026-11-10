// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// GET /api/auth/account/deletion-preview: what the "Delete account" card needs to explain the
/// consequences BEFORE the user commits - and to say honestly when deletion is not possible.
/// </summary>
public class AccountDeletionPreviewDto
{
    /// <summary>False when the account cannot be deleted right now; <see cref="UnavailableReason"/>
    /// then says why. The card shows the reason instead of the delete button.</summary>
    public bool Available { get; set; }

    /// <summary>Stable code the client switches on: <c>demo</c> (public demo instance, every write is
    /// refused) or <c>owner-has-other-users</c> (the owner administers the installation and
    /// cannot leave while other accounts exist). Null while <see cref="Available"/>.</summary>
    public string? UnavailableReason { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>Other accounts on this installation (a count only - never names or ids).</summary>
    public int OtherUserCount { get; set; }

    /// <summary>Developer-registered apps (OAuth clients) owned by this account. They are deleted with
    /// it, and every other user's integration with them stops working.</summary>
    public int OwnedClientCount { get; set; }

    /// <summary>True when this is the only account: deleting it returns the installation to its
    /// first-run state (set-up screen, new setup code in the server log).</summary>
    public bool InstallationWillReset { get; set; }
}

/// <summary>Answer to a successful DELETE /api/auth/account or DELETE /api/auth/users/{id}.</summary>
public class AccountDeletionResultDto
{
    /// <summary>The deleted account was the last one: the installation is back at first run.</summary>
    public bool InstallationReset { get; set; }

    /// <summary>Add-on clean-ups (studylife-ai, studylife-webhooks, studylife-developers) that could
    /// not be delivered right now and are retried in the background. The account itself is
    /// already gone either way.</summary>
    public int AddonCleanupPending { get; set; }
}

/// <summary>One account in GET /api/auth/users (owner-only list behind the "remove user" action).
/// Deliberately minimal: an id for the delete call and the display name the owner confirms by.</summary>
public class UserListItemDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
