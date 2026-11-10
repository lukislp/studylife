// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// Additive bridge for native app shells (MAUI Blazor Hybrid): WebAuthn isn't available in native
/// WebViews (especially iOS WKWebView, Android system WebView), so Login/Register/Link hand off
/// the entire passkey ceremony to the system browser
/// (ASWebAuthenticationSession/Custom Tabs) when a native implementation is registered.
/// In the normal browser client IsAvailable is always false (NoNativeAppAuth) - the existing
/// web flow stays exactly unchanged.
/// </summary>
public interface INativeAppAuth
{
    bool IsAvailable { get; }

    /// <summary>
    /// Opens the given auth page ("login", "register#name=...&amp;secret=...",
    /// "link#code=...") in the system browser on the configured server domain and waits for
    /// the callback. Return value: the session token; empty string = completed successfully
    /// but deliberately without a token (device link flow, device stays logged out until
    /// approval); null = cancelled or failed.
    /// </summary>
    Task<string?> AuthenticateAsync(string startPage);

    /// <summary>
    /// True if the app shell can run the passkey ceremony DIRECTLY inside the app
    /// (iOS with a paid developer profile including the associated-domains entitlement - Face ID
    /// without a browser sheet). In that case the auth pages run their normal web flow and only
    /// redirect the two WebAuthn calls to Create-/GetPasskey...Async instead of the
    /// JS module. Default false: browser flow via AuthenticateAsync.
    /// </summary>
    bool SupportsInAppPasskeys => false;

    /// <summary>Native passkey creation. optionsJson = CredentialCreateOptions.ToJson()
    /// from Fido2NetLib; return value in the format of AuthenticatorAttestationRawResponse
    /// (like createPasskey in Login.razor.js). null = cancelled/failed.</summary>
    Task<string?> CreatePasskeyAsync(string optionsJson) => Task.FromResult<string?>(null);

    /// <summary>Native passkey sign-in. optionsJson = AssertionOptions.ToJson() from
    /// Fido2NetLib; return value in the format of AuthenticatorAssertionRawResponse
    /// (like getPasskeyAssertion in Login.razor.js). null = cancelled/failed.</summary>
    Task<string?> GetPasskeyAssertionAsync(string optionsJson) => Task.FromResult<string?>(null);

    /// <summary>
    /// Called once after the user deleted their account (Setup, "Delete account"): the shell wipes what
    /// it keeps outside the web view - home-screen widget snapshot, Spotlight index, Watch snapshot.
    /// Logging out does not need this (those copies belong to the device, not the account), but a deleted
    /// account must not keep showing its notes and courses on the lock screen. Default: nothing to wipe.
    /// A default interface member on purpose: no new service registration for the native apps.
    /// </summary>
    Task AccountDeletedAsync() => Task.CompletedTask;
}

/// <summary>Default registration in the browser client: no native flow exists there.</summary>
public sealed class NoNativeAppAuth : INativeAppAuth
{
    public bool IsAvailable => false;
    public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
}
