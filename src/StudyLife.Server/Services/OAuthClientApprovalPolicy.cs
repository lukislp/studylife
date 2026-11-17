// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The review rules for instance-wide OAuth client registrations (2026-11-16 audit M5), kept
/// free of I/O so they live in exactly one place: any signed-in user can register a client, and
/// every user's consent screen then trusts that registration, so only the instance owner's
/// word makes it connectable.
/// </summary>
public static class OAuthClientApprovalPolicy
{
    /// <summary>The owner's own registrations need no second look; everyone else's wait.</summary>
    public static OAuthClientApproval ForNewRegistration(bool registrantIsOwner) =>
        registrantIsOwner ? OAuthClientApproval.Approved : OAuthClientApproval.Pending;

    /// <summary>An edit by the owner approves what the owner just wrote. An edit by anyone else
    /// that changes what the consent screen shows (name, description, redirect URIs, scopes)
    /// sends the client back to review - otherwise an approved, harmless-looking registration
    /// could be swapped for a different redirect target after the fact. The same rule turns an
    /// edited rejected client into a fresh request for the owner. A no-op save keeps the current
    /// state.</summary>
    public static OAuthClientApproval AfterEdit(OAuthClientApproval current, bool changed, bool editorIsOwner)
    {
        if (editorIsOwner) return OAuthClientApproval.Approved;
        return changed ? OAuthClientApproval.Pending : current;
    }

    public static string ToWire(OAuthClientApproval approval) => approval switch
    {
        OAuthClientApproval.Approved => OAuthClientApprovalStates.Approved,
        OAuthClientApproval.Rejected => OAuthClientApprovalStates.Rejected,
        _ => OAuthClientApprovalStates.Pending,
    };
}
