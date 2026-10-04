using StudyLife.Server.Configuration;

namespace StudyLife.Server.Discovery;

/// <summary>
/// What gets announced, exactly (docs/MDNS.md - the Home Assistant side depends on this contract):
/// service type <c>_studylife._tcp</c>, the instance name, the port of the advertised URL and the
/// four TXT records version/url/https/path, plus the optional fifth <c>id</c> (the stable instance id). Nothing else is ever put on the wire - no key, token,
/// user data or database detail.
/// </summary>
public sealed record MdnsServiceDescription(string InstanceName, int Port, IReadOnlyList<string> TxtRecords)
{
    public const string ServiceType = "_studylife._tcp";
    public const string FullServiceType = "_studylife._tcp.local.";

    /// <summary>The same description with the instance id added (or replaced); unchanged when
    /// <paramref name="id"/> is not 32 lowercase hex characters.</summary>
    public MdnsServiceDescription WithId(string? id)
    {
        if (!Services.InstanceIdProvider.IsValidId(id)) return this;
        var txt = TxtRecords.Where(t => !t.StartsWith("id=", StringComparison.Ordinal)).ToList();
        txt.Add($"id={id}");
        return this with { TxtRecords = txt };
    }

    /// <summary>The instance id of this description, or null when it carries none.</summary>
    public string? Id => TxtRecords.FirstOrDefault(t => t.StartsWith("id=", StringComparison.Ordinal))?[3..];

    /// <summary>The advertised base URL without trailing slash, e.g. https://studylife.example.org
    /// (scheme + host, plus the port only when it is not the scheme default).</summary>
    public static string NormalizeUrl(Uri url) =>
        url.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    /// <summary>Builds the description from validated options. Throws when the options are not
    /// valid (callers validate at startup first).</summary>
    public static MdnsServiceDescription From(MdnsOptions options, string version, string? id = null)
    {
        var url = options.ParsedUrl
            ?? throw new InvalidOperationException("Discovery:Mdns:Url ist keine absolute http(s)-URL.");
        // Uri.Port already resolves 443 for https and 80 for http when the URL has no explicit port.
        var https = url.Scheme == Uri.UriSchemeHttps;
        var txt = new List<string>
        {
            $"version={version}",
            $"url={NormalizeUrl(url)}",
            $"https={(https ? "true" : "false")}",
            "path=/",
        };
        // A malformed id is dropped rather than announced: Home Assistant would store garbage.
        if (Services.InstanceIdProvider.IsValidId(id)) txt.Add($"id={id}");
        var name = string.IsNullOrWhiteSpace(options.Name) ? "StudyLife" : options.Name.Trim();
        return new MdnsServiceDescription(name, url.Port, txt);
    }

    /// <summary>The running server version without the "+commit" build metadata.</summary>
    public static string CurrentVersion()
    {
        var informational = typeof(MdnsServiceDescription).Assembly
            ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "dev";
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
