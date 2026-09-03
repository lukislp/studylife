using System.Net;

namespace StudyLife.Server.Services;

/// <summary>
/// Validation for URLs the SERVER will later connect to on a user's behalf - today the Web Push
/// endpoint a browser hands PushController.Subscribe. Such a URL used to be stored verbatim and
/// POSTed to from the worker on every reminder cycle, so a caller could point it at
/// http://10.0.0.5:8080/... or a cloud metadata address and use the server as a blind
/// request proxy into the cluster network (2026-09 audit S4). Push services are always public
/// https origins, so the rule is: absolute https, bounded length, and no host that is a literal
/// loopback/private/link-local address or an obviously internal name. Hostnames are NOT resolved
/// here (a DNS lookup on the request path would add latency and a flaky dependency to every
/// subscribe) - DNS-rebinding to a private address is an accepted residual risk given that the
/// worker's only outbound action is a signed, opaque push payload with no response body exposed.
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
        return IsPublicHost(uri);
    }

    private static bool IsPublicHost(Uri uri)
    {
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
            return IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IsPublicAddress(ip);

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        // Kubernetes service DNS (studylife-web.studylife-scale.svc.cluster.local and friends).
        if (host.Contains(".svc.", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)) return false;
        // A bare single-label name ("redis", "postgres") only ever resolves inside a private network.
        return host.Contains('.');
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (unique local), fe80::/10 (link-local), plus multicast and unspecified.
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;
            if (b[0] == 0xFF) return false;
            return true;
        }
        var o = ip.GetAddressBytes();
        return o[0] switch
        {
            0 => false,                                    // 0.0.0.0/8
            10 => false,                                   // RFC1918
            127 => false,                                  // loopback
            100 when o[1] >= 64 && o[1] <= 127 => false,   // 100.64.0.0/10 carrier-grade NAT (Tailscale lives here)
            169 when o[1] == 254 => false,                 // link-local incl. cloud metadata 169.254.169.254
            172 when o[1] >= 16 && o[1] <= 31 => false,    // RFC1918
            192 when o[1] == 168 => false,                 // RFC1918
            >= 224 => false,                               // multicast + reserved + broadcast
            _ => true,
        };
    }
}
