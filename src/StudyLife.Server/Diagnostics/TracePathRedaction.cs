// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace StudyLife.Server.Diagnostics;

/// <summary>
/// Keeps bearer credentials that travel in a URL path out of exported traces. The read-only
/// progress link (<c>GET /api/progress/shared/{token}</c>) carries its token in the path, and the
/// ASP.NET Core instrumentation records the raw path as <c>url.path</c> - so the token would
/// otherwise land in Tempo for anyone with trace access. The query string (the ICS feed's
/// <c>?calendarToken=</c>) is already redacted by the instrumentation itself.
/// </summary>
public static class TracePathRedaction
{
    private const string UrlPathTag = "url.path";
    private static readonly PathString SharedProgressPrefix = "/api/progress/shared";

    /// <summary>The path to record for <paramref name="path"/>: the route template instead of
    /// the raw path when the path carries a credential, otherwise null (record as is).</summary>
    public static string? RedactedPath(PathString path) =>
        path.StartsWithSegments(SharedProgressPrefix, out var rest) && rest.HasValue && rest.Value != "/"
            ? SharedProgressPrefix + "/{token}"
            : null;

    /// <summary>Hook for <c>AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest</c>.</summary>
    public static void Enrich(Activity activity, HttpRequest request)
    {
        if (RedactedPath(request.Path) is { } redacted)
            activity.SetTag(UrlPathTag, redacted);
    }
}
