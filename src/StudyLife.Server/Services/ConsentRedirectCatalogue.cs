// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services;

/// <summary>
/// The single place that knows which first-party callbacks the consent flows trust by default
/// (2026-11 audit M5). <see cref="ConsentRedirectPolicy"/> merges these with operator
/// configuration (Consent:ChromeExtensionIds:&lt;audience&gt;); operators can add IDs but never
/// remove the defaults.
/// </summary>
public static class ConsentRedirectCatalogue
{
    /// <summary>studylife-capture's Chrome Web Store listing.</summary>
    public const string CaptureExtensionId = "glhegeoapkifmhodpnbfjgijlflkdglh";

    /// <summary>The stable ID studylife-focus gets from the key in its manifest
    /// (lukislp/studylife-focus#151); serves both the focusguard and focustunes audiences.</summary>
    public const string FocusExtensionId = "hhgnenpnafffiadgabcclncnbfdopnlp";

    /// <summary>Audiences whose callback is a loopback URL of a native app (RFC 8252 §7.3).</summary>
    public static readonly IReadOnlySet<string> LoopbackAudiences =
        new HashSet<string>(StringComparer.Ordinal) { "mcp", "tray" };

    /// <summary>The only path studylife-mcp (login.py) and studylife-tray (ConnectFlow.cs) listen on.</summary>
    public const string LoopbackCallbackPath = "/callback";

    /// <summary>Built-in extension IDs per browser-extension audience (keys are the extension audiences).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultChromeExtensionIds =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["capture"] = [CaptureExtensionId],
            ["focusguard"] = [FocusExtensionId],
            ["focustunes"] = [FocusExtensionId],
        };
}
