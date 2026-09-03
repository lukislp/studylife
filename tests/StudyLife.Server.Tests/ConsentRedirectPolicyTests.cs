using Microsoft.Extensions.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Per-audience redirect_uri allow-list for the consent connect flow (2026-09 audit S1). The
/// integration half (an unlisted https callback is rejected end-to-end at mcp-connect) lives in
/// ConsentAudienceAndLoopbackTests; this class pins the policy's own decision table.
/// </summary>
public class ConsentRedirectPolicyTests
{
    private static ConsentRedirectPolicy Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ConsentRedirectPolicy(config);
    }

    [Theory]
    [InlineData("mcp", "http://127.0.0.1:8765/callback")]
    [InlineData("mcp", "http://localhost:1/x")]
    [InlineData("tray", "http://127.0.0.1:51823/callback")]
    public void LoopbackAudiences_AcceptLoopback_WithoutConfig(string audience, string uri) =>
        Assert.True(Build().IsAllowed(audience, uri));

    [Theory]
    [InlineData("capture")]
    [InlineData("focusguard")]
    [InlineData("focustunes")]
    public void ExtensionAudiences_AcceptChromiumappCallback_WithoutConfig(string audience) =>
        Assert.True(Build().IsAllowed(audience, "https://abcdefghijklmnopqrstuvwxyzabcdef.chromiumapp.org/"));

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
