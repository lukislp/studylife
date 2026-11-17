// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>What a link card (calendar subscription, progress share) can show.</summary>
public enum OneTimeLinkView
{
    /// <summary>No link exists: offer to create one.</summary>
    None,
    /// <summary>The token was just created and is in hand: show the link once, with "copy it now".</summary>
    Shown,
    /// <summary>A link exists, but the server stores only its hash: it cannot be shown again,
    /// only replaced by regenerating.</summary>
    Hidden,
}

/// <summary>
/// The calendar and share tokens are stored on the server only as hashes (audit 2026-11-16 L5),
/// so the link is shown exactly once, right after it was created or regenerated. This decides
/// which state a card shows from what it knows; the cards only render it.
/// </summary>
public static class OneTimeLink
{
    /// <param name="exists">The server reports an active link.</param>
    /// <param name="token">The plaintext token, only present right after create/regenerate (or
    /// from a server older than L5, which still returns it).</param>
    public static OneTimeLinkView Of(bool exists, string? token) =>
        !exists ? OneTimeLinkView.None
        : string.IsNullOrEmpty(token) ? OneTimeLinkView.Hidden
        : OneTimeLinkView.Shown;
}
