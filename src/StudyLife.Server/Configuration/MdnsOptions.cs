using Microsoft.Extensions.Options;

namespace StudyLife.Server.Configuration;

/// <summary>
/// Opt-in mDNS / DNS-SD announcement (docs/MDNS.md): lets Home Assistant (zeroconf) discover the
/// server as <c>_studylife._tcp</c> on the LAN. Off by default - nothing is announced and no socket
/// is opened unless <see cref="Enabled"/> or <see cref="Only"/> is set. Env mapping as everywhere:
/// Discovery__Mdns__Enabled, Discovery__Mdns__Url, Discovery__Mdns__Name, Discovery__Mdns__Only.
/// </summary>
public sealed class MdnsOptions
{
    public const string SectionName = "Discovery:Mdns";

    /// <summary>Announce this process on the LAN in addition to serving normally.</summary>
    public bool Enabled { get; set; }

    /// <summary>The advertised base URL (scheme + host[:port]). Configuration, deliberately NOT
    /// derived from the pod/host: the server usually sits behind an ingress/gateway under a name
    /// like https://studylife.example.org and Home Assistant has to connect to that.</summary>
    public string? Url { get; set; }

    /// <summary>Instance name shown by DNS-SD browsers.</summary>
    public string Name { get; set; } = "StudyLife";

    /// <summary>Run ONLY the announcer (no database, no web endpoints) - the dedicated hostNetwork
    /// pod of k8s/optional/studylife-mdns.yaml, announcing on behalf of the ingress URL.</summary>
    public bool Only { get; set; }

    /// <summary>True when the announcer has to run at all.</summary>
    public bool Active => Enabled || Only;

    /// <summary>The parsed <see cref="Url"/>, or null when it is missing or not an absolute
    /// http/https URL.</summary>
    public Uri? ParsedUrl =>
        Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;
}

/// <summary>Startup validation of <see cref="MdnsOptions"/>: a nonsense announcement URL must stop
/// the process with a readable message (same German wording as the other startup checks) instead
/// of silently advertising something Home Assistant cannot connect to. Nothing is validated while
/// the feature is off.</summary>
public sealed class MdnsOptionsValidator : IValidateOptions<MdnsOptions>
{
    public ValidateOptionsResult Validate(string? name, MdnsOptions options)
    {
        if (!options.Active)
            return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(options.Url))
            return ValidateOptionsResult.Fail(
                "Discovery:Mdns:Url muss gesetzt sein, wenn Discovery:Mdns:Enabled oder Discovery:Mdns:Only aktiv ist "
                + "(die Basis-URL, unter der Home Assistant den Server erreicht, z. B. https://studylife.example.org).");
        if (options.ParsedUrl is null)
            return ValidateOptionsResult.Fail(
                $"Discovery:Mdns:Url '{options.Url}' ist keine absolute http(s)-URL (erwartet z. B. https://studylife.example.org).");
        if (string.IsNullOrWhiteSpace(options.Name))
            return ValidateOptionsResult.Fail("Discovery:Mdns:Name darf nicht leer sein.");
        // A DNS label is limited to 63 octets.
        if (System.Text.Encoding.UTF8.GetByteCount(options.Name.Trim()) > 63)
            return ValidateOptionsResult.Fail("Discovery:Mdns:Name darf höchstens 63 Bytes lang sein (DNS-Label-Limit).");
        return ValidateOptionsResult.Success;
    }
}
