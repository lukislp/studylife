// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// Decides which <c>?returnUrl=</c> a successful sign-in may navigate to. Only a target on the
/// app's own origin is accepted, and it is returned as a root-relative path (path, query,
/// fragment). The check resolves the URL the way the browser will and compares origins, instead
/// of pattern-matching the string: <c>/\evil.example</c> and <c>/%5Cevil.example</c> look like
/// paths but browsers treat <c>\</c> as <c>/</c>, so they would leave the origin.
/// </summary>
public static class ReturnUrlPolicy
{
    /// <summary>The same-origin root-relative path for <paramref name="returnUrl"/>, or null
    /// when it is empty, not root-relative, or would leave <paramref name="appBaseUri"/>'s
    /// origin.</summary>
    public static string? ToSameOriginPath(string? returnUrl, Uri appBaseUri)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
        {
            return null;
        }

        // Browsers fold '\' into '/' and drop tabs/newlines before parsing, which turns
        // "/\host" and "/\thost"-style inputs into protocol-relative URLs. Neither has a
        // legitimate use in an in-app path, so refuse them outright (also percent-encoded,
        // in case the value was decoded once more on the way).
        if (returnUrl.Any(c => c == '\\' || char.IsControl(c))
            || returnUrl.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(appBaseUri, returnUrl, out var resolved)
            || Uri.Compare(resolved, appBaseUri, UriComponents.SchemeAndServer, UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            return null;
        }

        return resolved.PathAndQuery + resolved.Fragment;
    }
}
