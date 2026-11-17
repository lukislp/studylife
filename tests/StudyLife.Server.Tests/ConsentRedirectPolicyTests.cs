// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Per-audience redirect_uri allow-list for the consent connect flow (2026-09 audit S1). The
/// integration half (an unlisted https callback is rejected end-to-end at mcp-connect) lives in
/// ConsentAudienceAndLoopbackTests; this class pins the policy's own decision table.
/// </summary>
public class ConsentRedirectPolicyTests
{
    private static ConsentRedirectPolicy Build(params (string Key, string Value)[] settings) =>
        new(TestOptions.MonitorFor<ConsentOptions>(
            settings.Select(s => (s.Key, (string?)s.Value)).ToArray()));

    [Theory]
    [InlineData("mcp", "http://127.0.0.1:8765/callback")]
    [InlineData("mcp", "http://localhost:1/callback")]
    [InlineData("mcp", "http://LOCALHOST:65535/callback")]
    [InlineData("tray", "http://127.0.0.1:51823/callback")]
    public void LoopbackAudiences_AcceptLoopbackCallback_WithoutConfig(string audience, string uri) =>
        Assert.True(Build().IsAllowed(audience, uri));

    [Theory]
    [InlineData("mcp", "http://127.0.0.1:8765/callback/")] // trailing slash is a different path
    [InlineData("mcp", "http://127.0.0.1:8765/")]
    [InlineData("mcp", "http://127.0.0.1:8765/other")]
    [InlineData("mcp", "http://localhost:1/x")]
    [InlineData("tray", "http://127.0.0.1:51823/callbackx")]
    [InlineData("tray", "http://127.0.0.1:51823/cb")]
    [InlineData("tray", "http://[::1]:51823/callback")] // only the literal hosts are accepted
    [InlineData("tray", "https://127.0.0.1:51823/callback")]
    [InlineData("mcp", "http://127.0.0.1:8765@evil.com/callback")]
    public void LoopbackAudiences_RefuseOtherPathsAndShapes(string audience, string uri) =>
        Assert.False(Build().IsAllowed(audience, uri));

    private const string CaptureId = "glhegeoapkifmhodpnbfjgijlflkdglh";
    private const string FocusId = "hhgnenpnafffiadgabcclncnbfdopnlp"; // unpacked builds (manifest key)
    private const string FocusStoreId = "bhjoeghhdmaoepmbklbcbdkfnjbgclbh"; // Chrome Web Store package
    private const string OtherId = "abcdefghijklmnopabcdefghijklmnop";

    [Theory]
    [InlineData("capture", CaptureId)]
    [InlineData("focusguard", FocusId)]
    [InlineData("focustunes", FocusId)]
    [InlineData("focusguard", FocusStoreId)]
    [InlineData("focustunes", FocusStoreId)]
    public void ExtensionAudiences_AcceptPinnedId_WithoutConfig(string audience, string id)
    {
        Assert.True(Build().IsAllowed(audience, $"https://{id}.chromiumapp.org/"));
        Assert.True(Build().IsAllowed(audience, $"https://{id.ToUpperInvariant()}.chromiumapp.org/")); // ID matched case-insensitively
        Assert.True(Build().IsAllowed(audience, $"https://{id}.chromiumapp.org")); // empty path normalizes to /
    }

    [Theory]
    [InlineData("capture", FocusId)] // another audience's ID
    [InlineData("capture", FocusStoreId)]
    [InlineData("focusguard", CaptureId)]
    [InlineData("capture", OtherId)] // well-formed but unknown
    public void ExtensionAudiences_RefuseUnpinnedId(string audience, string id) =>
        Assert.False(Build().IsAllowed(audience, $"https://{id}.chromiumapp.org/"));

    [Theory]
    [InlineData("https://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/cb")] // sub-path
    [InlineData("https://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/?x=1")] // only the root URL chrome.identity returns
    [InlineData("http://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/")] // not https
    [InlineData("https://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org:8443/")] // odd port
    [InlineData("https://user@glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/")] // userinfo
    [InlineData("https://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org.evil.com/")] // look-alike host
    [InlineData("https://evilglhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/")] // prefix look-alike
    [InlineData("https://x.glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org/")] // sub-domain
    [InlineData("https://glhegeoapkifmhodpnbfjgijlflkdglh.chromiumapp.org@evil.com/")] // userinfo confusion
    public void Capture_RefusesMalformedShapes_EvenForPinnedId(string uri) =>
        Assert.False(Build().IsAllowed("capture", uri));

    [Fact]
    public void ConfiguredExtensionId_IsAdded_AndDefaultsStay()
    {
        var policy = Build(("Consent:ChromeExtensionIds:capture:0", OtherId));

        Assert.True(policy.IsAllowed("capture", $"https://{OtherId}.chromiumapp.org/"));
        Assert.True(policy.IsAllowed("capture", $"https://{CaptureId}.chromiumapp.org/")); // never silently removed
        Assert.False(policy.IsAllowed("focusguard", $"https://{OtherId}.chromiumapp.org/")); // per audience
    }

    [Fact]
    public void ConfiguredExtensionId_IsNormalized_AndMalformedOnesAreIgnored()
    {
        var policy = Build(
            ("Consent:ChromeExtensionIds:capture:0", $" {OtherId.ToUpperInvariant()} "),
            ("Consent:ChromeExtensionIds:capture:1", "short"),
            ("Consent:ChromeExtensionIds:capture:2", new string('z', 32))); // outside a-p

        Assert.True(policy.IsAllowed("capture", $"https://{OtherId}.chromiumapp.org/"));
        Assert.Equal(2, policy.ChromeExtensionIdsFor("capture").Count); // default + the one valid addition
    }

    [Fact]
    public void RejectionMessage_ForUnpinnedExtension_NamesTheConfigKey()
    {
        Assert.Contains("Consent:ChromeExtensionIds:capture",
            Build().RejectionMessage("capture", $"https://{OtherId}.chromiumapp.org/"));
        Assert.DoesNotContain("Consent:ChromeExtensionIds",
            Build().RejectionMessage("mcp", "https://attacker.example/cb"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9999/callback", true)] // other port, same path
    [InlineData("http://127.0.0.1:1/callback", true)]
    [InlineData("http://127.0.0.1:8765/callback", true)] // exact
    [InlineData("http://127.0.0.1:9999/other", false)] // different path
    [InlineData("http://127.0.0.1:9999/callback?x=1", false)] // query differs
    [InlineData("http://localhost:9999/callback", false)] // different host
    [InlineData("https://127.0.0.1:9999/callback", false)]
    [InlineData("http://127.0.0.1:9999@evil.com/callback", false)]
    [InlineData("http://127.0.0.1:9999/callback#frag", false)]
    [InlineData("https://example-addon.test/callback", true)]
    [InlineData("https://example-addon.test:8443/callback", false)] // non-loopback stays exact
    [InlineData("https://example-addon.test/callback/", false)]
    public void RegisteredMatch_LoopbackIgnoresPortOnly(string requested, bool expected) =>
        Assert.Equal(expected, ConsentRedirectPolicy.MatchesRegisteredRedirectUri(
            ["https://example-addon.test/callback", "http://127.0.0.1:8765/callback"], requested));

    [Fact]
    public void RegisteredMatch_ClientWithoutLoopbackRegistration_NeverGetsLoopback() =>
        Assert.False(ConsentRedirectPolicy.MatchesRegisteredRedirectUri(
            ["https://example-addon.test/callback"], "http://127.0.0.1:9999/callback"));

    [Fact]
    public void RegisteredMatch_QueryOnRegisteredLoopback_MustMatch()
    {
        string[] registered = ["http://127.0.0.1:8765/cb?client=a"];
        Assert.True(ConsentRedirectPolicy.MatchesRegisteredRedirectUri(registered, "http://127.0.0.1:1/cb?client=a"));
        Assert.False(ConsentRedirectPolicy.MatchesRegisteredRedirectUri(registered, "http://127.0.0.1:1/cb?client=b"));
        Assert.False(ConsentRedirectPolicy.MatchesRegisteredRedirectUri(registered, "http://127.0.0.1:1/cb"));
    }

    [Theory]
    [InlineData("mcp", "https://mcp.example.com/auth/studylife/callback")] // the open-redirect shape that used to pass
    [InlineData("mcp", "https://attacker.example/cb")]
    [InlineData("tray", "https://attacker.example/cb")]
    [InlineData("capture", "https://attacker.example/cb")]
    [InlineData("capture", "http://127.0.0.1:8765/callback")] // extensions never use loopback
    [InlineData("capture", "https://chromiumapp.org/")] // bare suffix host, no extension id
    [InlineData("capture", "https://evil.example/?x=.chromiumapp.org")] // suffix only counts on the host
    [InlineData("focusguard", "https://notchromiumapp.org/")]
    [InlineData("unknown-audience", "https://anything.example/")]
    public void UnlistedTargets_AreRejected(string audience, string uri) =>
        Assert.False(Build().IsAllowed(audience, uri));

    [Fact]
    public void ConfiguredUri_IsAccepted_ExactMatchOnly()
    {
        var policy = Build(("Consent:AllowedRedirectUris:mcp:0", "https://mcp.example.com/auth/studylife/callback"));

        Assert.True(policy.IsAllowed("mcp", "https://mcp.example.com/auth/studylife/callback"));
        Assert.False(policy.IsAllowed("mcp", "https://mcp.example.com/auth/studylife/callback/"));
        Assert.False(policy.IsAllowed("mcp", "https://mcp.example.com/auth/studylife/callback?x=1"));
        Assert.False(policy.IsAllowed("mcp", "https://MCP.example.com/auth/studylife/callback"));
        // Listed for mcp does not make it valid for another audience.
        Assert.False(policy.IsAllowed("capture", "https://mcp.example.com/auth/studylife/callback"));
    }

    [Fact]
    public void ConfiguredUri_ForUnknownAudience_IsAccepted()
    {
        var policy = Build(("Consent:AllowedRedirectUris:some-future-audience:0", "https://future.example/cb"));
        Assert.True(policy.IsAllowed("some-future-audience", "https://future.example/cb"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void NonAbsoluteInput_IsRejected(string? uri) =>
        Assert.False(Build(("Consent:AllowedRedirectUris:mcp:0", "")).IsAllowed("mcp", uri));
}
