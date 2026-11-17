// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// Which stored, third-party-supplied URLs may become a clickable <c>href</c> outside rendered
/// Markdown: a note's capture <c>SourceUrl</c> and a marketplace listing's repository link.
/// Stricter than <see cref="MarkdownUrlPolicy"/>: only an absolute <c>http</c> or <c>https</c>
/// URL with a host is a web link. <c>javascript:</c>, <c>data:</c>, app schemes, relative and
/// scheme-relative targets are all refused, so one click can never run script in the app or
/// navigate the app's own origin.
/// </summary>
public static class WebLinkPolicy
{
    /// <summary>True when <paramref name="url"/> is an absolute http(s) URL with a host.</summary>
    public static bool IsWebLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Uri trims surrounding whitespace itself; inner tabs/newlines would be stripped by the
        // browser ("java\tscript:"), so a URL that contains any control character is refused.
        if (url.Any(char.IsControl))
        {
            return false;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary><paramref name="url"/> when it is a web link, otherwise null - for write paths
    /// that degrade instead of rejecting (backup import).</summary>
    public static string? WebLinkOrNull(string? url) => IsWebLink(url) ? url!.Trim() : null;
}
