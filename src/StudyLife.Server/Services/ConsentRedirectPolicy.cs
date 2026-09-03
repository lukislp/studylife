using Microsoft.Extensions.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Decides which redirect_uri a consent "connect" action (AuthController.5.Consent.cs) may hand a
/// single-use assertion to. The five hardcoded audiences used to accept ANY absolute https URL:
/// the assertion-exchange endpoints are anonymous and the assertion is their only credential, so
/// whoever receives the browser redirect can redeem it for the freshly rotated API key. A link
/// like /connect/mcp?redirect_uri=https://attacker.example/cb was therefore a one-click key
/// theft (2026-09 audit, finding S1). The set of legitimate callbacks per audience is small and
/// known, so this narrows the check to exactly those shapes:
///
/// - mcp, tray: the RFC 8252 §8.3 loopback exception (http://127.0.0.1|localhost:&lt;any port&gt;/...,
///   studylife-mcp's `mcp --login` and studylife-tray's ConnectFlow), plus any exact URI listed
///   under Consent:AllowedRedirectUris:&lt;audience&gt; - the HTTP-mode studylife-mcp server's own
///   /auth/studylife/callback lives there, since its public host is deployment-specific.
/// - capture, focusguard, focustunes: chrome.identity.getRedirectURL(), i.e.
///   https://&lt;extension-id&gt;.chromiumapp.org/..., plus configured exact URIs. Only an extension
///   installed in the user's own browser can ever receive a chromiumapp.org redirect, so the
///   host suffix is the natural trust boundary without pinning a store-specific extension id.
/// - any other audience: configured exact URIs only.
///
/// Dynamically registered OAuth clients (AuthController.10.OAuthClients.cs) are NOT routed
/// through here - they already match against their own registered AllowedRedirectUris.
/// </summary>
public sealed class ConsentRedirectPolicy
{
    public const string ConfigSectionName = "Consent:AllowedRedirectUris";

    private static readonly HashSet<string> LoopbackAudiences = new(StringComparer.Ordinal) { "mcp", "tray" };
    private static readonly HashSet<string> ChromeExtensionAudiences = new(StringComparer.Ordinal) { "capture", "focusguard", "focustunes" };

    private readonly IConfiguration _config;

    public ConsentRedirectPolicy(IConfiguration config) => _config = config;

    public bool IsAllowed(string audience, string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;

        if (LoopbackAudiences.Contains(audience) && IsLoopback(uri)) return true;
        if (ChromeExtensionAudiences.Contains(audience) && IsChromeExtensionCallback(uri)) return true;

        var configured = _config.GetSection($"{ConfigSectionName}:{audience}").Get<string[]>() ?? [];
        return configured.Contains(redirectUri!, StringComparer.Ordinal);
    }

    /// <summary>EXACTLY http://127.0.0.1:&lt;port&gt;/... or http://localhost:&lt;port&gt;/... - never any
    /// other http host (see AuthController.IsAllowedRedirectUri, which keeps the same rule as
    /// the syntactic gate shared with DeveloperController's registration validation).</summary>
    public static bool IsLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public static bool IsChromeExtensionCallback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".chromiumapp.org", StringComparison.OrdinalIgnoreCase)
        && uri.Host.Length > ".chromiumapp.org".Length;
}
