// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// The trust decision behind the IP-partitioned rate limiter (2026-09 audit S6): which
/// upstreams may set X-Forwarded-For. Pins the loopback + container-gateway default (2026-11-16
/// audit M8: no longer all of RFC1918) and the configured paths.
/// </summary>
public class ForwardedHeadersConfigTests
{
    // Bound through the same section name and options class Program.cs uses, so the keys under
    // test stay the real "ForwardedHeaders:..." configuration keys.
    private static TrustedProxyOptions Config(params (string Key, string Value)[] settings) =>
        TestOptions.Configuration(settings.Select(s => (s.Key, (string?)s.Value)).ToArray())
            .Bind<TrustedProxyOptions>(TrustedProxyOptions.SectionName);

    private static readonly IPAddress[] NoGateway = [];

    private static bool Trusts(Microsoft.AspNetCore.Builder.ForwardedHeadersOptions options, string ip)
    {
        var address = IPAddress.Parse(ip);
        return options.KnownProxies.Contains(address) || options.KnownIPNetworks.Any(n => n.Contains(address));
    }

    [Fact]
    public void Default_TrustsLoopbackAndTheContainerGatewayOnly()
    {
        var options = ForwardedHeadersConfig.Build(Config(), [IPAddress.Parse("172.17.0.1")]);

        Assert.True(Trusts(options, "127.0.0.1"));
        Assert.True(Trusts(options, "::1"));
        Assert.True(Trusts(options, "172.17.0.1"));
        Assert.Equal(1, options.ForwardLimit);
    }

    /// <summary>Audit M8: any private address used to be trusted, so a sibling container or a LAN
    /// host could send X-Forwarded-For and get a fresh rate-limit bucket per request.</summary>
    [Theory]
    [InlineData("10.42.3.7")]
    [InlineData("172.17.0.2")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("8.8.8.8")]
    public void Default_NoLongerTrustsOtherPrivateAddresses(string ip)
    {
        var options = ForwardedHeadersConfig.Build(Config(), [IPAddress.Parse("172.17.0.1")]);

        Assert.False(Trusts(options, ip));
    }

    [Fact]
    public void HonoursForwardedForAndProto_ButNotHost_ByDefault()
    {
        var options = ForwardedHeadersConfig.Build(defaultGateways: NoGateway, trustedProxies: Config());

        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
    }

    [Fact]
    public void ForwardHost_AddsXForwardedHost()
    {
        // X-Forwarded-Host matters for passkeys behind a proxy that rewrites Host: AuthController.CreateFido2
        // derives the relying-party id and origin from Request.Host, and GitHub Codespaces' port
        // forwarder sends localhost:<port> (launchSettings.json switches this on for development).
        var options = ForwardedHeadersConfig.Build(defaultGateways: NoGateway, trustedProxies: Config(("ForwardedHeaders:ForwardHost", "true")));

        Assert.True(options.ForwardedHeaders.HasFlag(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost));
    }

    [Fact]
    public void AConfiguredProxy_ReplacesTheGatewayDefault()
    {
        var options = ForwardedHeadersConfig.Build(Config(("ForwardedHeaders:KnownProxies:0", "192.168.20.200")), [IPAddress.Parse("172.17.0.1")]);

        Assert.True(Trusts(options, "192.168.20.200"));
        Assert.False(Trusts(options, "172.17.0.1"));
    }

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("172.17.0.1", true)]
    [InlineData("fd00::1", true)]
    public void GatewayDiscovery_SkipsPlaceholderAndLinkLocalAddresses(string ip, bool usable) =>
        Assert.Equal(usable, DefaultGateways.IsUsable(IPAddress.Parse(ip)));

    [Fact]
    public void ConfiguredNetworks_ReplaceTheDefaults_InsteadOfExtendingThem()
    {
        var options = ForwardedHeadersConfig.Build(defaultGateways: NoGateway, trustedProxies: Config(("ForwardedHeaders:KnownNetworks:0", "10.42.3.0/24")));

        Assert.True(Trusts(options, "10.42.3.200"));
        Assert.False(Trusts(options, "10.42.4.1"));
        Assert.False(Trusts(options, "192.168.1.1")); // default range no longer trusted
        Assert.False(Trusts(options, "127.0.0.1"));
    }

    [Fact]
    public void ConfiguredProxies_AndForwardLimit_AreApplied()
    {
        var options = ForwardedHeadersConfig.Build(defaultGateways: NoGateway, trustedProxies: Config(
            ("ForwardedHeaders:KnownProxies:0", "203.0.113.10"),
            ("ForwardedHeaders:KnownNetworks:0", "173.245.48.0/20"), // a Cloudflare range, as the demo host would list
            ("ForwardedHeaders:ForwardLimit", "2")));

        Assert.True(Trusts(options, "203.0.113.10"));
        Assert.True(Trusts(options, "173.245.50.1"));
        Assert.False(Trusts(options, "10.0.0.1"));
        Assert.Equal(2, options.ForwardLimit);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.42.3.0")] // missing prefix
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.42.3.0/33")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "not-a-network/24")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "gateway.local")]
    [InlineData("ForwardedHeaders:ForwardLimit", "0")]
    public void InvalidConfiguration_FailsFastAtStartup(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersConfig.Build(defaultGateways: NoGateway, trustedProxies: Config((key, value))));
}
