// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;

namespace StudyLife.Server.Services;

/// <summary>
/// Registration-time validation for URLs the server (or an add-on) will later connect to on a
/// user's behalf - the Web Push endpoint a browser hands PushService.SubscribeAsync and the webhook
/// target registered through WebhooksProxyController. Such a URL used to be stored verbatim and
/// POSTed to unattended, so a caller could point it at http://10.0.0.5:8080/... or a cloud metadata
/// address and use the server as a blind request proxy into the cluster network (2026-09 audit S4).
/// The rule here is syntactic: absolute http(s), bounded length, no credentials, and no host that is
/// a literal non-public address or an obviously internal name. It gives the user a clear error up
/// front; it is NOT the enforcement point, because a public name can resolve to a private address
/// (2026-11-16 audit M4). The enforcement is at connect time: the server sends Web Push only through
/// UserSuppliedUrlHttp (resolve, vet, connect to the vetted address), and studylife-webhooks does
/// the same for webhook deliveries.
/// </summary>
public static class OutboundUrlPolicy
{
    public const int MaxLength = 2048;

    public static bool IsAcceptablePushEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        var isPublic = IsPublicHost(uri);
        // SSRF guard (audit S4): an accepted push endpoint is always a credential-free https URL.
        Debug.Assert(!isPublic || (uri.Scheme == Uri.UriSchemeHttps && uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo)),
            "an accepted push endpoint must be absolute, https and credential-free");
        return isPublic;
    }

    /// <summary>
    /// Same class of URL as the push endpoint above, one hop further away: a webhook target
    /// registered through WebhooksProxyController is stored by studylife-webhooks and POSTed to
    /// from there on every matching event, and studylife-webhooks runs INSIDE the cluster network
    /// with no validation of its own (2026-09-11 audit). Unlike push services, a user's own
    /// receiver is legitimately allowed to be plain http (a home server behind a public
    /// hostname), so only the scheme rule is relaxed - the private/loopback/link-local/cluster-DNS
    /// host rules are identical.
    /// </summary>
    public static bool IsAcceptableWebhookTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return IsPublicHost(uri);
    }

    private static bool IsPublicHost(Uri uri)
    {
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
            return IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && Outbound.OutboundAddressClassifier.IsPublic(ip);

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        // Kubernetes service DNS (studylife-web.studylife-scale.svc.cluster.local and friends).
        if (host.Contains(".svc.", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)) return false;
        // A bare single-label name ("redis", "postgres") only ever resolves inside a private network.
        return host.Contains('.');
    }
}
