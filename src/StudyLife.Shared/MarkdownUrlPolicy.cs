// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// Which link and image targets a rendered note may carry. Notes can be written by other
/// parties than the reader (AI, MCP and capture keys, backup import), so a URL is untrusted:
/// only <c>http</c>, <c>https</c> and <c>mailto</c> are allowed, plus scheme-less targets
/// (relative paths, <c>#anchors</c>, <c>?query</c>, <c>//host</c>). Everything else -
/// <c>javascript:</c>, <c>vbscript:</c>, <c>data:</c>, <c>file:</c>, app-specific schemes - is
/// refused. An allow list, not a block list: an unknown scheme is refused by default.
/// </summary>
public static class MarkdownUrlPolicy
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>True when <paramref name="url"/> may be emitted as an <c>href</c> or
    /// <c>src</c>. A null or empty URL is allowed: it points at the current page.</summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var normalized = NormalizeLikeBrowser(url);

        // The scheme is everything before the first ':' - but only if that ':' comes before any
        // '/', '?' or '#'; otherwise the target is relative ("notes/a:b", "#x:y", "?q=a:b").
        var end = normalized.IndexOfAny([':', '/', '?', '#']);
        if (end < 0 || normalized[end] != ':')
        {
            return true;
        }

        var scheme = normalized[..end];
        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    // What the WHATWG URL parser does before it looks for a scheme: drop every ASCII tab and
    // newline anywhere ("java\tscript:" is javascript:) and trim leading/trailing C0 controls
    // and spaces. Anything else in the would-be scheme (non-ASCII, '%', '&') makes it an
    // invalid scheme, which the allow list then refuses - stricter than the browser, on purpose.
    private static string NormalizeLikeBrowser(string url)
    {
        var withoutTabsAndNewlines = url.Replace("\t", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal);
        return withoutTabsAndNewlines.Trim(C0ControlsAndSpace);
    }

    private static readonly char[] C0ControlsAndSpace =
        Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray();
}
