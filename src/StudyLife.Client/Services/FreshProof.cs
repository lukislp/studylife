// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>How the primary "confirm it's you" step is done on this device.</summary>
public enum FreshProofPrimary
{
    /// <summary>No passkey route: only the current password or a recovery code can confirm.</summary>
    None,
    /// <summary>The web page runs the WebAuthn prompt itself (browser client).</summary>
    WebPasskey,
    /// <summary>The native shell runs the passkey ceremony inside the app.</summary>
    InAppPasskey,
    /// <summary>A native shell without in-app passkeys (free signing tier): sign in again through the
    /// system browser; the fresh sign-in is the proof (the server accepts a sign-in of the last
    /// two minutes).</summary>
    BrowserSignIn,
}

/// <summary>Which proofs to offer for a step that needs a fresh proof of identity (the server's
/// VerifyFreshProofAsync): the recommended primary route, the current password (only when the account
/// has a usable one) and the recovery code (always).</summary>
public sealed record FreshProofOptions(FreshProofPrimary Primary, bool CurrentPassword, bool RecoveryCode = true)
{
    /// <summary>The pure decision. <paramref name="hasPasskey"/> gates the passkey routes; a browser
    /// sign-in does not need one (the sign-in page offers whatever the account has).</summary>
    public static FreshProofOptions Decide(bool nativeAvailable, bool inAppPasskeys, bool hasPasskey, bool hasUsablePassword)
    {
        var primary = (nativeAvailable, inAppPasskeys, hasPasskey) switch
        {
            (true, false, _) => FreshProofPrimary.BrowserSignIn,
            (true, true, true) => FreshProofPrimary.InAppPasskey,
            (false, _, true) => FreshProofPrimary.WebPasskey,
            _ => FreshProofPrimary.None,
        };
        return new FreshProofOptions(primary, hasUsablePassword);
    }
}

/// <summary>What the user proved. All members null = rely on a sign-in within the last two minutes
/// (<see cref="IsFreshSession"/>, after a browser sign-in).</summary>
public sealed record FreshProof(string? OptionsId = null, string? AssertionJson = null,
    string? RecoveryCode = null, string? CurrentPassword = null)
{
    public bool IsFreshSession => OptionsId is null && AssertionJson is null && RecoveryCode is null && CurrentPassword is null;
}

/// <summary>The texts of a FreshProofPicker, taken from the host's own i18n table.</summary>
public sealed record FreshProofLabels(
    string Intro, string BrowserSignInIntro,
    string PasskeyButton, string BrowserSignInButton,
    string PasswordToggle, string PasswordLabel, string PasswordButton,
    string RecoveryToggle, string RecoveryLabel, string RecoveryButton,
    string Working, string PasskeyFailed);
