// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Services.Outbound;

namespace StudyLife.Server.Tests;

/// <summary>Test filter: public addresses plus loopback (for local fake servers). Used by
/// CustomWebApplicationFactory and the connect tests below.</summary>
internal sealed class LoopbackOrPublicAddressFilter : IOutboundAddressFilter
{
    public bool IsAllowed(IPAddress address) =>
        IPAddress.IsLoopback(address) || OutboundAddressClassifier.IsPublic(address);
}

/// <summary>Resolver that answers from a fixed table and counts lookups.</summary>
internal sealed class FakeHostResolver(Dictionary<string, IPAddress[]> table) : IHostAddressResolver
{
    public int Lookups;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Lookups);
        return Task.FromResult(table.TryGetValue(host, out var addresses) ? addresses : []);
    }

    public static FakeHostResolver Of(string host, params string[] addresses) =>
        new(new Dictionary<string, IPAddress[]> { [host] = addresses.Select(IPAddress.Parse).ToArray() });
}

/// <summary>
/// A raw HTTP/1.1 server on 127.0.0.1 that records the request head and answers with a fixed
/// response. Raw on purpose: HttpListener rejects a Host header that does not match its prefix,
/// and these tests send a host NAME that only the fake resolver maps to 127.0.0.1.
/// </summary>
internal sealed class RawHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _response;
    public int Port { get; }
    public List<string> RequestHeads { get; } = [];

    public RawHttpServer(string response = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
    {
        _response = response;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                lock (RequestHeads) RequestHeads.Add(head.ToString());
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_response));
            }
        }
    }

    public void Dispose() => _listener.Stop();
}

public class OutboundAddressClassifierTests
{
    [Theory]
    // IPv4 non-public
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.42.0.17")]          // k3s pod CIDR
    [InlineData("10.43.0.10")]          // k3s service CIDR (kube-dns)
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.251")]         // mDNS multicast
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6 non-public
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0:4136:e378::1")] // Teredo
    // IPv6 spellings of private IPv4
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:100.64.0.1")]
    [InlineData("::10.0.0.1")]          // IPv4-compatible
    [InlineData("64:ff9b::a00:1")]      // NAT64 of 10.0.0.1
    [InlineData("64:ff9b::7f00:1")]     // NAT64 of 127.0.0.1
    [InlineData("2002:a00:1::1")]       // 6to4 of 10.0.0.1
    [InlineData("2002:c0a8:101::1")]    // 6to4 of 192.168.1.1
    public void NonPublicAddresses_AreRefused(string ip) =>
        Assert.False(OutboundAddressClassifier.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]      // just below CGNAT
    [InlineData("100.128.0.1")]         // just above CGNAT
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4001::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]    // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]     // 6to4 of 8.8.8.8
    public void PublicAddresses_AreAllowed(string ip) =>
        Assert.True(OutboundAddressClassifier.IsPublic(IPAddress.Parse(ip)));
}

public class VettedConnectorTests
{
    private static VettedConnector Connector(FakeHostResolver resolver) => new(resolver, PublicAddressFilter.Instance);

    [Fact]
    public async Task PublicName_ReturnsItsAddresses()
    {
        var addresses = await Connector(FakeHostResolver.Of("push.example.com", "8.8.8.8", "2606:4700::1111"))
            .ResolveVettedAsync("push.example.com", CancellationToken.None);
        Assert.Equal(2, addresses.Count);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.100")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.0.1")]
    public async Task NameResolvingToPrivate_IsRefused(string ip)
    {
        var ex = await Assert.ThrowsAsync<OutboundAddressRejectedException>(() =>
            Connector(FakeHostResolver.Of("evil.example.com", ip)).ResolveVettedAsync("evil.example.com", CancellationToken.None));
        Assert.Equal("evil.example.com", ex.Host);
    }

    [Fact]
    public async Task MixedPublicAndPrivateAnswer_IsRefusedAsAWhole()
    {
        await Assert.ThrowsAsync<OutboundAddressRejectedException>(() =>
            Connector(FakeHostResolver.Of("mixed.example.com", "8.8.8.8", "10.0.0.1"))
                .ResolveVettedAsync("mixed.example.com", CancellationToken.None));
        await Assert.ThrowsAsync<OutboundAddressRejectedException>(() =>
            Connector(FakeHostResolver.Of("mixed6.example.com", "2606:4700::1111", "fe80::1"))
                .ResolveVettedAsync("mixed6.example.com", CancellationToken.None));
    }

    [Fact]
    public async Task UnresolvableName_IsRefused() =>
        await Assert.ThrowsAsync<OutboundAddressRejectedException>(() =>
            Connector(FakeHostResolver.Of("other.example.com", "8.8.8.8")).ResolveVettedAsync("nothing.example.com", CancellationToken.None));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("::ffff:10.1.2.3")]
    public async Task IpLiteral_IsVettedWithoutDns(string host)
    {
        var resolver = FakeHostResolver.Of("unused", "8.8.8.8");
        await Assert.ThrowsAsync<OutboundAddressRejectedException>(() => Connector(resolver).ResolveVettedAsync(host, CancellationToken.None));
        Assert.Equal(0, resolver.Lookups);
    }
}

/// <summary>The handler from UserSuppliedUrlHttp end to end, against a local raw server.</summary>
public class UserSuppliedUrlHttpHandlerTests
{
    [Fact]
    public async Task ConnectsToTheVettedAddress_WithTheOriginalHostHeader()
    {
        using var server = new RawHttpServer();
        // A name system DNS cannot resolve: the request can only arrive if the handler connected to
        // the address the vetting resolver returned.
        var resolver = FakeHostResolver.Of("push.studylife.invalid", "127.0.0.1");
        using var http = new HttpClient(UserSuppliedUrlHttp.CreateHandler(resolver, new LoopbackOrPublicAddressFilter()));

        using var response = await http.PostAsync($"http://push.studylife.invalid:{server.Port}/send/abc", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var head = Assert.Single(server.RequestHeads);
        Assert.StartsWith("POST /send/abc HTTP/1.1", head);
        Assert.Contains($"Host: push.studylife.invalid:{server.Port}", head);
        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public async Task NameResolvingToLoopback_NeverOpensAConnection()
    {
        using var server = new RawHttpServer();
        var resolver = FakeHostResolver.Of("rebind.studylife.invalid", "127.0.0.1");
        using var http = new HttpClient(UserSuppliedUrlHttp.CreateHandler(resolver, PublicAddressFilter.Instance));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.PostAsync($"http://rebind.studylife.invalid:{server.Port}/", new StringContent("{}")));

        Assert.IsType<OutboundAddressRejectedException>(ex.InnerException);
        await Task.Delay(100);
        Assert.Empty(server.RequestHeads);
    }

    [Fact]
    public async Task MixedAnswer_NeverOpensAConnection()
    {
        using var server = new RawHttpServer();
        var resolver = FakeHostResolver.Of("mixed.studylife.invalid", "8.8.8.8", "127.0.0.1");
        using var http = new HttpClient(UserSuppliedUrlHttp.CreateHandler(resolver, PublicAddressFilter.Instance));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync($"http://mixed.studylife.invalid:{server.Port}/"));

        Assert.IsType<OutboundAddressRejectedException>(ex.InnerException);
        Assert.Empty(server.RequestHeads);
    }

    [Fact]
    public async Task Redirects_AreNotFollowed()
    {
        using var server = new RawHttpServer(
            "HTTP/1.1 307 Temporary Redirect\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var resolver = FakeHostResolver.Of("push.studylife.invalid", "127.0.0.1");
        using var http = new HttpClient(UserSuppliedUrlHttp.CreateHandler(resolver, new LoopbackOrPublicAddressFilter()));

        using var response = await http.PostAsync($"http://push.studylife.invalid:{server.Port}/", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Single(server.RequestHeads);
    }

    [Fact]
    public void Handler_DisablesRedirectsAndProxy()
    {
        using var handler = UserSuppliedUrlHttp.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void Registration_DefaultsToSystemDnsAndPublicOnly()
    {
        using var provider = new ServiceCollection()
            .AddUserSuppliedUrlHttpClient()
            .BuildServiceProvider();
        Assert.IsType<PublicAddressFilter>(provider.GetRequiredService<IOutboundAddressFilter>());
        Assert.IsType<DnsHostAddressResolver>(provider.GetRequiredService<IHostAddressResolver>());
        var client = provider.GetRequiredService<UserSuppliedUrlHttpClient>();
        Assert.Same(client, provider.GetRequiredService<UserSuppliedUrlHttpClient>());
        Assert.Equal(UserSuppliedUrlHttp.RequestTimeout, client.Http.Timeout);
    }
}
