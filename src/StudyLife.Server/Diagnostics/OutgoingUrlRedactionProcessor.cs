// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using OpenTelemetry;

namespace StudyLife.Server.Diagnostics;

/// <summary>
/// Keeps the addresses of outgoing requests out of exported traces (audit 2026-11-19 S10). The
/// HttpClient instrumentation records the full URL of every client span as <c>url.full</c>; for the
/// push senders that URL is the credential: a web-push endpoint is a per-device capability URL and
/// an APNs request path ends in the device token. Tempo would keep them for the retention period.
/// <para>
/// Only the scheme and host survive, plus the path of calls to the in-cluster add-ons
/// (<c>/internal/...</c>, which carry no credential and are what one wants to see in a trace).
/// Everything else is recorded as <c>https://host/{redacted}</c>, the query string never. This runs
/// as a processor on the finished span, so it holds whichever component (the runtime's own
/// instrumentation or the OpenTelemetry one) set the tag.
/// </para>
/// </summary>
public sealed class OutgoingUrlRedactionProcessor : BaseProcessor<Activity>
{
    internal const string Redacted = "/{redacted}";

    private static readonly string[] UrlTags = ["url.full", "http.url"];
    private static readonly string[] DroppedTags = ["url.query", "http.target"];
    private const string InternalPrefix = "/internal/";

    /// <summary>The address to record for <paramref name="url"/>; null when it is not an absolute URL (then the tag is dropped).</summary>
    public static string? Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return uri.AbsolutePath.StartsWith(InternalPrefix, StringComparison.Ordinal)
            ? origin + uri.AbsolutePath
            : origin + Redacted;
    }

    public override void OnEnd(Activity activity)
    {
        if (activity.Kind != ActivityKind.Client) return;
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is string url)
                activity.SetTag(tag, Redact(url));
        }
        foreach (var tag in DroppedTags)
        {
            if (activity.GetTagItem(tag) is not null) activity.SetTag(tag, null);
        }
    }
}
