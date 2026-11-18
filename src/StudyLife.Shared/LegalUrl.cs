// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace StudyLife.Shared;

/// <summary>The one rule for an operator-supplied legal page address: an absolute https URL without
/// credentials (http only for a loopback host, for local development). Used by the server to refuse
/// a bad setting at startup and by the client before it renders a link from the server's answer.</summary>
public static class LegalUrl
{
    public static bool IsValid(string? value) => TryParse(value, out _);

    public static bool TryParse(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || string.IsNullOrEmpty(parsed.Host)) return false;
        var ok = parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && IsLoopback(parsed));
        if (ok) uri = parsed;
        return ok;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
