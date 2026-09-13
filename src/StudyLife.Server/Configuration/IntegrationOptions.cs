namespace StudyLife.Server.Configuration;

/// <summary>
/// studylife-ai integration (see <see cref="Services.AiProxyClient"/> for the full contract,
/// including the audit-A5 secret split and the /internal port cutover).
/// </summary>
public sealed class StudyLifeAiOptions
{
    public const string SectionName = "StudyLifeAi";

    /// <summary>Public port serving /chat|/agent|/agent/confirm.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Dedicated /internal/* port; falls back to <see cref="BaseUrl"/> when unset.</summary>
    public string? InternalBaseUrl { get; set; }

    /// <summary>Legacy single secret; still accepted as a fallback for the two below.</summary>
    public string? SharedSecret { get; set; }

    /// <summary>Comma-separated "kid:secret" entries; the first signs new proxy tokens.</summary>
    public string? TokenSigningSecret { get; set; }

    /// <summary>Raw bearer sent as X-StudyLife-Shared-Secret to /internal/*.</summary>
    public string? InternalApiSecret { get; set; }
}

/// <summary>studylife-webhooks integration (see <see cref="Services.WebhooksProxyClient"/>).</summary>
public sealed class StudyLifeWebhooksOptions
{
    public const string SectionName = "StudyLifeWebhooks";

    public string? BaseUrl { get; set; }
    public string? SharedSecret { get; set; }
}

/// <summary>studylife-developers integration (see <see cref="Services.DeveloperProxyClient"/>).</summary>
public sealed class StudyLifeDevelopersOptions
{
    public const string SectionName = "StudyLifeDevelopers";

    public string? BaseUrl { get; set; }
    public string? SharedSecret { get; set; }
}

/// <summary>
/// APNs delivery channel for the native iOS shell (see <see cref="Services.ApnsSender"/>). The
/// channel stays inert until KeyPath/KeyId/TeamId/BundleId are all set - that "power switch"
/// stays a runtime check rather than a DataAnnotation, because "none of them set" is the
/// supported default, not a misconfiguration.
/// </summary>
public sealed class ApnsOptions
{
    public const string SectionName = "Apns";

    public string? KeyPath { get; set; }
    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    public string? BundleId { get; set; }

    /// <summary>Overrides the target URL - test use only.</summary>
    public string? Endpoint { get; set; }

    /// <summary>True targets api.sandbox.push.apple.com (development-profile installs).</summary>
    public bool UseSandbox { get; set; }
}

/// <summary>Apple App Site Association. Without a team id the well-known endpoint 404s.</summary>
public sealed class AppleOptions
{
    public const string SectionName = "Apple";

    public string? TeamId { get; set; }
}
