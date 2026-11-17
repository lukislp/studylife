// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Builds the ForwardedHeadersOptions Program.cs hands to UseForwardedHeaders - i.e. WHICH
/// upstream addresses are trusted to tell Kestrel the real client IP (X-Forwarded-For), scheme
/// (X-Forwarded-Proto) and host (X-Forwarded-Host). The IP-partitioned rate limiter (and the strict recovery-code
/// limiter in particular) is only as good as this trust decision: anything on the trusted list
/// can forge a fresh client IP per request and get a fresh limiter bucket every time.
///
/// Defaults (nothing configured, 2026-11-16 audit M8): loopback plus the container's own default
/// gateway (<see cref="DefaultGateways"/>) - a reverse proxy on the same host reaches a published
/// Docker port through the bridge gateway. Everything else must be configured; trusting all of
/// RFC1918, as before, let anything on the LAN or a sibling container mint a fresh rate-limit
/// bucket per request. A reverse proxy on another machine (Nginx Proxy Manager, a Kubernetes
/// gateway) is listed explicitly (env form in parentheses):
///
///   ForwardedHeaders:KnownNetworks:N  (ForwardedHeaders__KnownNetworks__N)  CIDR, e.g. 10.42.0.0/16
///   ForwardedHeaders:KnownProxies:N   (ForwardedHeaders__KnownProxies__N)   single IP
///   ForwardedHeaders:ForwardLimit     (ForwardedHeaders__ForwardLimit)      hops to walk, default 1
///   ForwardedHeaders:ForwardHost      (ForwardedHeaders__ForwardHost)       also X-Forwarded-Host, default false
///
/// Listing either networks or proxies REPLACES the defaults entirely (no silent union - that
/// would defeat the point of narrowing). An untrusted proxy is visible: its X-Forwarded-Proto is
/// ignored, so the HTTPS redirection answers every request with a redirect, and
/// <see cref="UntrustedForwardedHeadersWarning"/> logs the address to add once. ForwardLimit &gt; 1 is for a proxy chain
/// such as Cloudflare -&gt; nginx -&gt; Kestrel: each hop walked must itself be on the trusted list,
/// so the public demo host adds Cloudflare's published ranges as KnownNetworks and sets 2.
///
/// Known limitation, documented rather than papered over: on a flat Kubernetes pod network
/// (k3s default 10.42.0.0/16) a CIDR cannot single out the gateway pods, because every pod -
/// including sibling namespaces the NetworkPolicy lets reach studylife-web - shares that
/// range. The residual exposure is limited to those first-party pods; the recovery limiter
/// additionally has an IP-independent global bucket (Program.cs) for exactly this reason.
/// </summary>
public static class ForwardedHeadersConfig
{
    public const string SectionName = "ForwardedHeaders";

    private static readonly (string Address, int PrefixLength)[] DefaultKnownNetworks =
    [
        ("127.0.0.0", 8),      // loopback
        ("::1", 128),          // loopback
    ];

    /// <param name="defaultGateways">The container's default gateways (<see cref="DefaultGateways.Discover"/>),
    /// trusted only while nothing is configured.</param>
    public static ForwardedHeadersOptions Build(TrustedProxyOptions trustedProxies, IEnumerable<IPAddress> defaultGateways)
    {
        var options = new ForwardedHeadersOptions
        {
            // X-Forwarded-Host only on request (ForwardHost): a proxy that rewrites the Host header on
            // the way in (GitHub Codespaces' port forwarder sends Host: localhost:<port>) would
            // otherwise make the passkey relying-party id and origin, both derived from
            // Request.Host in AuthController.CreateFido2, come out as "localhost". Everywhere else
            // the gateway keeps the original Host, and a forwarded one is only an extra lever.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                               | (trustedProxies.ForwardHost ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.None),
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var networks = trustedProxies.KnownNetworks;
        var proxies = trustedProxies.KnownProxies;

        if (networks.Length == 0 && proxies.Length == 0)
        {
            foreach (var (address, prefixLength) in DefaultKnownNetworks)
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(address), prefixLength));
            foreach (var gateway in defaultGateways)
                options.KnownProxies.Add(gateway);
        }
        else
        {
            foreach (var cidr in networks)
                options.KnownIPNetworks.Add(ParseCidr(cidr));
            foreach (var proxy in proxies)
            {
                if (!IPAddress.TryParse(proxy.Trim(), out var ip))
                    throw new InvalidOperationException($"{SectionName}:KnownProxies contains '{proxy}', which is not an IP address.");
                options.KnownProxies.Add(ip);
            }
        }

        var forwardLimit = trustedProxies.ForwardLimit;
        if (forwardLimit is not null)
        {
            if (forwardLimit < 1)
                throw new InvalidOperationException($"{SectionName}:ForwardLimit must be at least 1 (got {forwardLimit}).");
            options.ForwardLimit = forwardLimit;
        }
        return options;
    }

    private static System.Net.IPNetwork ParseCidr(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new InvalidOperationException($"{SectionName}:KnownNetworks contains '{cidr}', which is not a CIDR (expected e.g. 10.42.3.0/24).");
        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > maxPrefix)
            throw new InvalidOperationException($"{SectionName}:KnownNetworks contains '{cidr}' with an out-of-range prefix length.");
        return new System.Net.IPNetwork(address, prefix);
    }
}
