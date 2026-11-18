// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace StudyLife.Client.Services;

/// <summary>
/// Decides whether a response from the app's own server ends the current session. Kept apart from
/// <see cref="SessionHandler"/> so the rule is testable without an HTTP pipeline.
///
/// A 401 only speaks for the session the request was SENT with. A request that left before a
/// sign-in (no token) or before a re-sign-in (older token) can come back with a 401 after the new
/// token is already stored - on a slow network, or on the very first start of the native app where
/// the dashboard's first requests race the demo/passkey sign-in. Such a late 401 says nothing about
/// the new session, so it must not discard it. A 401 for the token that is still current (real
/// expiry or revocation), or a 401 while there is still no session at all (fresh browser: the user
/// has to be sent to the sign-in page), keeps ending the session as before.
/// </summary>
public static class SessionInvalidationPolicy
{
    public static bool EndsSession(Uri? requestUri, Uri baseAddress, HttpStatusCode status, string? tokenSent, string? currentToken)
    {
        if (status != HttpStatusCode.Unauthorized) return false;
        if (requestUri is null || !baseAddress.IsBaseOf(requestUri)) return false;
        // /api/auth: a 401 there means "this sign-in step failed", not "your session is dead".
        if (IsAuthPath(requestUri)) return false;
        return string.Equals(Normalize(tokenSent), Normalize(currentToken), StringComparison.Ordinal);
    }

    public static bool IsAuthPath(Uri requestUri) =>
        requestUri.AbsolutePath.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? token) => string.IsNullOrEmpty(token) ? null : token;
}
