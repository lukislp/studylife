// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// Version of the AI consent dialog's disclosure text (<c>AiConsentText.*.json</c> in the client).
/// It lives in Shared so that every client build carries the version of the text it actually
/// renders: the client sends it with the grant and the server refuses a grant for text older than
/// its own, instead of recording a consent for disclosures the user never saw. Raise it whenever
/// the dialog starts disclosing something materially new; the server folds it into the consent
/// fingerprint, so every stored consent then stops covering and users are asked again.
/// History: 1 = original text, 2 = added third-country transfer, agent checkpoint retention
/// (30 days) and capture enrichment (2026-11-19 audit).
/// </summary>
public static class AiConsentTextVersion
{
    public const int Current = 2;

    /// <summary>
    /// Version assumed for a grant that carries none: clients from before the handshake that
    /// already showed text 2 (3.39.x). Fixed forever; a bump of <see cref="Current"/> therefore
    /// makes those clients be asked to update (the dialog is shown again, nothing is recorded).
    /// </summary>
    public const int AssumedWhenMissing = 2;
}