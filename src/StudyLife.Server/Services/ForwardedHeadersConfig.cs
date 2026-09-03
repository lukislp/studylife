using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace StudyLife.Server.Services;

/// <summary>
/// Builds the ForwardedHeadersOptions Program.cs hands to UseForwardedHeaders - i.e. WHICH
/// upstream addresses are trusted to tell Kestrel the real client IP (X-Forwarded-For) and
/// scheme (X-Forwarded-Proto). The IP-partitioned rate limiter (and the strict recovery-code
/// limiter in particular) is only as good as this trust decision: anything on the trusted list
/// can forge a fresh client IP per request and get a fresh limiter bucket every time.
///
/// Defaults (nothing configured) are the RFC1918 + loopback ranges the single-container
/// docker-compose/Pi deployment has always used, unchanged. Where the topology is known, the
/// operator narrows it via configuration (env form in parentheses):
///
///   ForwardedHeaders:KnownNetworks:N  (ForwardedHeaders__KnownNetworks__N)  CIDR, e.g. 10.42.3.0/24
///   ForwardedHeaders:KnownProxies:N   (ForwardedHeaders__KnownProxies__N)   single IP
///   ForwardedHeaders:ForwardLimit     (ForwardedHeaders__ForwardLimit)      hops to walk, default 1
///
/// Listing either networks or proxies REPLACES the defaults entirely (no silent union with
/// RFC1918 - that would defeat the point of narrowing). ForwardLimit &gt; 1 is for a proxy chain
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
        ("10.0.0.0", 8),       // RFC1918
        ("172.16.0.0", 12),    // RFC1918 (covers Docker's default bridge range)
        ("192.168.0.0", 16),   // RFC1918
    ];

    public static ForwardedHeadersOptions Build(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var section = configuration.GetSection(SectionName);
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];

        if (networks.Length == 0 && proxies.Length == 0)
        {
            foreach (var (address, prefixLength) in DefaultKnownNetworks)
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(address), prefixLength));
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

        var forwardLimit = section.GetValue<int?>("ForwardLimit");
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
