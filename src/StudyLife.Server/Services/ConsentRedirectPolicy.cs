// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Decides which redirect_uri a consent "connect" action (AuthController.5.Consent.cs) may hand a
/// single-use assertion to. The five hardcoded audiences used to accept ANY absolute https URL:
/// the assertion-exchange endpoints are anonymous and the assertion is their only credential, so
/// whoever receives the browser redirect can redeem it for the freshly rotated API key. A link
/// like /connect/mcp?redirect_uri=https://attacker.example/cb was therefore a one-click key
/// theft (2026-09 audit, finding S1). The set of legitimate callbacks per audience is small and
/// known, so this narrows the check to exactly those shapes (2026-11 audit M5 pinned them
/// further - the extension ID and the loopback path, not just the host family):
///
/// - mcp, tray: the RFC 8252 loopback exception, pinned to the callback path:
///   http://127.0.0.1|localhost:&lt;any port&gt;/callback (studylife-mcp's `mcp --login`,
///   studylife-tray's ConnectFlow). Any port is fine (the app picks a free one), any other path
///   is refused. Plus any exact URI listed under Consent:AllowedRedirectUris:&lt;audience&gt; -
///   the HTTP-mode studylife-mcp server's own /auth/studylife/callback lives there, since its
///   public host is deployment-specific.
/// - capture, focusguard, focustunes: chrome.identity.getRedirectURL(), i.e.
///   https://&lt;extension-id&gt;.chromiumapp.org/ (root path only - no first-party extension passes
///   a sub-path), where the extension ID must be pinned for that audience. The pinned set is the
///   built-in <see cref="ConsentRedirectCatalogue"/> defaults PLUS operator additions under
///   Consent:ChromeExtensionIds:&lt;audience&gt; (unpacked builds have their own ID). Being "some
///   extension" is not enough: any extension installed in a victim's browser could otherwise
///   receive the assertion. Plus configured exact URIs.
/// - any other audience: configured exact URIs only.
///
/// Dynamically registered OAuth clients (AuthController.10.OAuthClients.cs) are NOT routed
/// through <see cref="IsAllowed"/> - they match against their own registered AllowedRedirectUris
/// via <see cref="MatchesRegisteredRedirectUri"/>: exact match, except that a loopback request
/// matches a registered loopback URI with the same scheme, host, path and query on any port (RFC
/// 8252 §7.3). Only clients that registered a loopback URI can receive loopback redirects.
/// </summary>
public sealed partial class ConsentRedirectPolicy
{
    public const string ConfigSectionName = ConsentOptions.SectionName + ":AllowedRedirectUris";
    public const string ChromeExtensionIdsConfigSectionName = ConsentOptions.SectionName + ":ChromeExtensionIds";

    private const string ChromiumAppSuffix = ".chromiumapp.org";

    // chrome.identity extension IDs are exactly 32 characters from the alphabet a-p.
    [GeneratedRegex("^[a-p]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionIdPattern();

    private readonly IOptionsMonitor<ConsentOptions> _consent;
    private readonly ILogger<ConsentRedirectPolicy>? _log;
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    // Singleton that used to re-read the configuration section on every check, so a monitor (not
    // a frozen IOptions snapshot) keeps a configuration reload taking effect exactly as before -
    // and CurrentValue is a cached field read instead of the former string lookup + rebind.
    public ConsentRedirectPolicy(IOptionsMonitor<ConsentOptions> consent, ILogger<ConsentRedirectPolicy>? log = null)
    {
        _consent = consent;
        _log = log;
    }

    public bool IsAllowed(string audience, string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;

        if (ConsentRedirectCatalogue.LoopbackAudiences.Contains(audience) && IsPinnedLoopbackCallback(uri)) return true;
        if (IsChromeExtensionAudience(audience) && IsPinnedChromeExtensionCallback(audience, uri)) return true;

        // Case-insensitive audience lookup on purpose: configuration keys themselves are
        // case-insensitive, so "Consent__AllowedRedirectUris__MCP__0" used to match audience
        // "mcp" and still has to. The bound dictionary is tiny (one entry per configured
        // audience), so the scan costs nothing worth a custom comparer.
        var configured = _consent.CurrentValue.AllowedRedirectUris
            .FirstOrDefault(entry => string.Equals(entry.Key, audience, StringComparison.OrdinalIgnoreCase))
            .Value ?? [];
        return configured.Contains(redirectUri!, StringComparer.Ordinal);
    }

    /// <summary>Reason text for a refused redirect_uri. For a chromiumapp.org callback of an
    /// unpinned extension it names the config key that fixes it (operators running an unpacked
    /// build with a different extension ID).</summary>
    public string RejectionMessage(string audience, string? redirectUri)
    {
        if (IsChromeExtensionAudience(audience)
            && Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            && IsChromeExtensionCallback(uri))
        {
            return "redirectUri is a chromiumapp.org callback of an extension that is not pinned for this audience. "
                + "Operators running an unpacked or differently signed build can add its extension ID under "
                + $"{ChromeExtensionIdsConfigSectionName}:{audience} (env Consent__ChromeExtensionIds__{audience}__0).";
        }
        return "redirectUri is not an allowed callback for this audience (see Consent:AllowedRedirectUris).";
    }

    /// <summary>The effective pinned extension IDs for an audience: built-in defaults plus valid
    /// configured additions, lower-cased. Malformed configured IDs are ignored with a warning.</summary>
    public IReadOnlySet<string> ChromeExtensionIdsFor(string audience)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (ConsentRedirectCatalogue.DefaultChromeExtensionIds.TryGetValue(audience, out var defaults))
            foreach (var id in defaults) ids.Add(id);

        var configured = _consent.CurrentValue.ChromeExtensionIds
            .FirstOrDefault(entry => string.Equals(entry.Key, audience, StringComparison.OrdinalIgnoreCase))
            .Value ?? [];
        foreach (var raw in configured)
        {
            var id = raw?.Trim().ToLowerInvariant();
            if (id is not null && ExtensionIdPattern().IsMatch(id))
            {
                ids.Add(id);
            }
            else if (_warned.TryAdd($"{audience}\n{raw}", 0))
            {
                _log?.LogWarning(
                    "Ignoring malformed {Section}:{Audience} entry '{Value}': a Chrome extension ID is exactly 32 characters a-p.",
                    ChromeExtensionIdsConfigSectionName, audience, raw);
            }
        }
        return ids;
    }

    public static bool IsChromeExtensionAudience(string audience) =>
        ConsentRedirectCatalogue.DefaultChromeExtensionIds.ContainsKey(audience);

    private bool IsPinnedChromeExtensionCallback(string audience, Uri uri)
    {
        if (!IsChromeExtensionCallback(uri)) return false;
        // chrome.identity.getRedirectURL() without an argument: https://<id>.chromiumapp.org/
        if (uri.AbsolutePath != "/" || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var id = uri.Host[..^ChromiumAppSuffix.Length].ToLowerInvariant();
        return ChromeExtensionIdsFor(audience).Contains(id);
    }

    private static bool IsPinnedLoopbackCallback(Uri uri) =>
        IsLoopback(uri) && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.AbsolutePath, ConsentRedirectCatalogue.LoopbackCallbackPath, StringComparison.Ordinal);

    /// <summary>
    /// Redirect match for a dynamically registered OAuth client. A non-loopback URI must equal a
    /// registered one exactly (ordinal). A loopback URI additionally matches a registered
    /// loopback URI whose scheme, host, path and query are equal, ignoring the port (RFC 8252
    /// §7.3: native apps pick a free port at runtime). A client with no loopback URI registered
    /// therefore never receives a loopback redirect.
    /// </summary>
    public static bool MatchesRegisteredRedirectUri(IEnumerable<string> registered, string? requested)
    {
        if (string.IsNullOrEmpty(requested)) return false;
        var list = registered as IReadOnlyCollection<string> ?? registered.ToList();
        if (list.Contains(requested, StringComparer.Ordinal)) return true;

        if (!Uri.TryCreate(requested, UriKind.Absolute, out var req)
            || !IsLoopback(req) || !string.IsNullOrEmpty(req.UserInfo) || req.Fragment.Length > 0) return false;

        foreach (var candidate in list)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var reg) || !IsLoopback(reg)) continue;
            if (string.Equals(reg.Scheme, req.Scheme, StringComparison.Ordinal)
                && string.Equals(reg.Host, req.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reg.AbsolutePath, req.AbsolutePath, StringComparison.Ordinal)
                && string.Equals(reg.Query, req.Query, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// SYNTACTIC redirect URI gate: an absolute https URL, or the RFC 8252 §8.3 native-app
    /// loopback exception (EXACTLY http://127.0.0.1:&lt;port&gt;/... or http://localhost:&lt;port&gt;/...,
    /// any port, any path). Nothing else non-https is ever accepted. This alone is NOT sufficient
    /// for the five hardcoded audiences anymore: the assertion-exchange endpoints are anonymous
    /// and the assertion is their only credential, so any https host that passed here could
    /// redeem it (2026-09 audit S1). AuthController.BuildConnectRedirectAsync therefore
    /// additionally requires <see cref="IsAllowed"/> - the per-audience allow-list. This stays
    /// the shared first-stage check, used as-is by DeveloperClientService when validating an
    /// OAuthClientEntity's AllowedRedirectUris at registration time (those are then matched by
    /// <see cref="MatchesRegisteredRedirectUri"/>). Static, and living here rather than on the
    /// controller, so the service that validates registrations doesn't have to reach into a
    /// controller for a rule that is already half this class's.
    /// </summary>
    public static bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return IsLoopback(uri);
    }

    /// <summary>EXACTLY http://127.0.0.1:&lt;port&gt;/... or http://localhost:&lt;port&gt;/... - never any
    /// other http host (see <see cref="IsAllowedRedirectUri"/>, the syntactic gate shared with
    /// DeveloperClientService's registration validation).</summary>
    public static bool IsLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public static bool IsChromeExtensionCallback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(ChromiumAppSuffix, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Length > ChromiumAppSuffix.Length;
}
