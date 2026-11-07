// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace StudyLife.Client.Services;

/// <summary>
/// What the "Report a problem" button puts into the GitHub bug-report form. ENVIRONMENT DATA
/// ONLY: app version, platform/browser family, UI language and whether the instance is the demo.
/// Never add tokens, user names, course data, URLs of the user's server or log lines here - the
/// link lands in a public issue tracker.
/// </summary>
/// <param name="Version">Release version of the running app (e.g. "3.26.5"), or null for a dev
/// or unstamped build.</param>
/// <param name="Platform">"web" in the browser, "app" in a native app shell.</param>
/// <param name="Browser">Short description such as "Chrome 142 on Windows" (see
/// <see cref="ProblemReportLink.DescribeUserAgent"/>); empty when unknown.</param>
/// <param name="Locale">UI language code, e.g. "de".</param>
/// <param name="Demo">True when the client talks to the public demo instance.</param>
public sealed record ProblemReportContext(
    string? Version, string Platform, string Browser, string Locale, bool Demo);

/// <summary>
/// Builds the URL that opens the repository's bug-report issue form with the environment
/// fields pre-filled (GitHub maps query parameters named after the form field ids onto the
/// fields). The field ids below must exist in <c>.github/ISSUE_TEMPLATE/bug_report.yml</c>;
/// <c>ProblemReportLinkTests</c> reads the file so form and code cannot drift apart.
/// </summary>
public static class ProblemReportLink
{
    public const string Repository = "lukislp/studylife";
    public const string TemplateFile = "bug_report.yml";

    // Field ids of the issue form.
    public const string VersionField = "version";
    public const string EnvironmentField = "environment";

    /// <summary>Soft cap for the whole URL. GitHub and browsers accept far more, but proxies
    /// and some shells choke around 8 KB, so stay clearly below.</summary>
    public const int MaxUrlLength = 6000;

    private const int MaxValueLength = 120;

    private static readonly Regex ReleaseVersion =
        new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True for a real release version. Dev and unstamped builds ("dev", "1.0.0",
    /// "0.0.0-...") map to no tag and therefore get no Codespaces line.</summary>
    public static bool IsReleaseVersion(string? version) =>
        version is not null
        && ReleaseVersion.IsMatch(version)
        && version != "1.0.0"
        && !version.StartsWith("0.0.0", StringComparison.Ordinal);

    /// <summary>The Codespaces link that opens exactly the tag of a release, or null.</summary>
    public static string? CodespacesUrl(string? version) =>
        IsReleaseVersion(version)
            ? $"https://codespaces.new/{Repository}?ref=v{version}&quickstart=1"
            : null;

    /// <summary>The multi-line text of the "Environment" form field.</summary>
    public static string BuildEnvironmentText(ProblemReportContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("App: ").Append(Clean(ctx.Version) is { Length: > 0 } v ? v : "dev").Append('\n');
        var browser = Clean(ctx.Browser);
        sb.Append("Platform: ").Append(Clean(ctx.Platform));
        if (browser.Length > 0) sb.Append(", ").Append(browser);
        sb.Append('\n');
        sb.Append("Locale: ").Append(Clean(ctx.Locale)).Append('\n');
        sb.Append("Demo: ").Append(ctx.Demo ? "yes" : "no");
        if (CodespacesUrl(ctx.Version) is { } codespaces)
            sb.Append("\nReproduce in Codespaces: ").Append(codespaces);
        return sb.ToString();
    }

    /// <summary>The full URL of the pre-filled bug-report form.</summary>
    public static string Build(ProblemReportContext ctx)
    {
        var version = Clean(ctx.Version);
        var url = Compose(version.Length > 0 ? version : "dev", BuildEnvironmentText(ctx));
        if (url.Length <= MaxUrlLength) return url;

        // Cannot happen with the per-value caps; kept as a hard guarantee for the URL size.
        var fallback = new ProblemReportContext(ctx.Version, "web", "", "", ctx.Demo);
        url = Compose(version.Length > 0 ? version : "dev", BuildEnvironmentText(fallback));
        return url.Length <= MaxUrlLength ? url : Compose("dev", "");
    }

    private static string Compose(string version, string environment) =>
        $"https://github.com/{Repository}/issues/new?template={TemplateFile}"
        + $"&{VersionField}={Uri.EscapeDataString(version)}"
        + (environment.Length > 0 ? $"&{EnvironmentField}={Uri.EscapeDataString(environment)}" : "");

    /// <summary>Single line, no control characters, capped length.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, MaxValueLength));
        foreach (var c in value)
        {
            if (sb.Length >= MaxValueLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Reduces a user-agent string to "Browser major on OS". Only the family and major
    /// version are kept, never the raw string.</summary>
    public static string DescribeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "";

        // Order matters: Edge and Opera also say Chrome, Chrome on iOS says CriOS, all say Safari.
        var browser =
            Family(userAgent, "Edg") is { } edge ? ("Edge", edge)
            : Family(userAgent, "OPR") is { } opera ? ("Opera", opera)
            : Family(userAgent, "Firefox") is { } ff ? ("Firefox", ff)
            : Family(userAgent, "FxiOS") is { } ffi ? ("Firefox", ffi)
            : Family(userAgent, "CriOS") is { } cri ? ("Chrome", cri)
            : Family(userAgent, "Chrome") is { } chrome ? ("Chrome", chrome)
            : Family(userAgent, "Version") is { } safari && userAgent.Contains("Safari/") ? ("Safari", safari)
            : ("", "");

        var os =
            userAgent.Contains("Windows") ? "Windows"
            : userAgent.Contains("Android") ? "Android"
            : userAgent.Contains("iPhone") || userAgent.Contains("iPad") ? "iOS"
            : userAgent.Contains("Mac OS") ? "macOS"
            : userAgent.Contains("CrOS") ? "ChromeOS"
            : userAgent.Contains("Linux") ? "Linux"
            : "";

        var name = browser.Item1.Length == 0 ? "" : $"{browser.Item1} {browser.Item2}".TrimEnd();
        if (name.Length == 0) return os;
        return os.Length == 0 ? name : $"{name} on {os}";
    }

    private static string? Family(string userAgent, string token)
    {
        var m = Regex.Match(userAgent, Regex.Escape(token) + @"/(\d+)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }
}
