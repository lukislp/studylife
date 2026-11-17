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
    /// <summary>studylife-capture's Chrome Web Store listing (the README's store links). Capture
    /// has no manifest key, so an unpacked build gets a path-derived ID that operators add via
    /// configuration.</summary>
    public const string CaptureStoreExtensionId = "glhegeoapkifmhodpnbfjgijlflkdglh";

    /// <summary>studylife-focus's Chrome Web Store listing.</summary>
    public const string FocusStoreExtensionId = "bhjoeghhdmaoepmbklbcbdkfnjbgclbh";

    /// <summary>The stable ID unpacked studylife-focus builds get from the key in their manifest
    /// (lukislp/studylife-focus#151). The store package is published without that key and keeps
    /// <see cref="FocusStoreExtensionId"/>.</summary>
    public const string FocusUnpackedExtensionId = "hhgnenpnafffiadgabcclncnbfdopnlp";

    /// <summary>Audiences whose callback is a loopback URL of a native app (RFC 8252 §7.3).</summary>
    public static readonly IReadOnlySet<string> LoopbackAudiences =
        new HashSet<string>(StringComparer.Ordinal) { "mcp", "tray" };

    /// <summary>The only path studylife-mcp (login.py) and studylife-tray (ConnectFlow.cs) listen on.</summary>
    public const string LoopbackCallbackPath = "/callback";

    /// <summary>Built-in extension IDs per browser-extension audience (keys are the extension audiences).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultChromeExtensionIds =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["capture"] = [CaptureStoreExtensionId],
            ["focusguard"] = [FocusStoreExtensionId, FocusUnpackedExtensionId],
            ["focustunes"] = [FocusStoreExtensionId, FocusUnpackedExtensionId],
        };
}
