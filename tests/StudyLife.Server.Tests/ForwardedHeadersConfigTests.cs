using System.Net;
using Microsoft.Extensions.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// The trust decision behind the IP-partitioned rate limiter (2026-09 audit S6): which
/// upstreams may set X-Forwarded-For. Pins the unchanged RFC1918 default and the configured
/// narrowing paths.
/// </summary>
public class ForwardedHeadersConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static bool Trusts(Microsoft.AspNetCore.Builder.ForwardedHeadersOptions options, string ip)
    {
        var address = IPAddress.Parse(ip);
        return options.KnownProxies.Contains(address) || options.KnownIPNetworks.Any(n => n.Contains(address));
    }

    [Fact]
    public void Default_TrustsPrivateAndLoopbackRangesOnly()
    {
        var options = ForwardedHeadersConfig.Build(Config());

        Assert.True(Trusts(options, "127.0.0.1"));
        Assert.True(Trusts(options, "10.42.3.7"));
        Assert.True(Trusts(options, "172.17.0.2"));
        Assert.True(Trusts(options, "192.168.1.1"));
        Assert.False(Trusts(options, "8.8.8.8"));
        Assert.False(Trusts(options, "100.64.0.1"));
        Assert.Equal(1, options.ForwardLimit);
    }

    [Fact]
    public void ConfiguredNetworks_ReplaceTheDefaults_InsteadOfExtendingThem()
    {
        var options = ForwardedHeadersConfig.Build(Config(("ForwardedHeaders:KnownNetworks:0", "10.42.3.0/24")));

        Assert.True(Trusts(options, "10.42.3.200"));
        Assert.False(Trusts(options, "10.42.4.1"));
        Assert.False(Trusts(options, "192.168.1.1")); // default range no longer trusted
        Assert.False(Trusts(options, "127.0.0.1"));
    }

    [Fact]
    public void ConfiguredProxies_AndForwardLimit_AreApplied()
    {
        var options = ForwardedHeadersConfig.Build(Config(
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
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersConfig.Build(Config((key, value))));
}
