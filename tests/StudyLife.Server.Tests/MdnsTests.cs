using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Discovery;

namespace StudyLife.Server.Tests;

/// <summary>
/// The opt-in mDNS announcement (docs/MDNS.md). No test opens a real socket: the announcer is
/// replaced by a fake, and the wire format is exercised as pure byte arrays.
/// </summary>
public class MdnsTests
{
    private sealed class FakeAnnouncer(bool failOnStart = false) : IMdnsAnnouncer
    {
        public int Starts;
        public int Stops;

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            if (failOnStart) throw new InvalidOperationException("no multicast");
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return Task.CompletedTask;
        }
    }

    private static IOptions<MdnsOptions> Options(params (string Key, string? Value)[] settings) =>
        TestOptions.For<MdnsOptions>(settings);

    private static MdnsServiceDescription Describe(string url, string? name = null)
    {
        var settings = new List<(string, string?)> { ("Discovery:Mdns:Enabled", "true"), ("Discovery:Mdns:Url", url) };
        if (name is not null) settings.Add(("Discovery:Mdns:Name", name));
        return MdnsServiceDescription.From(Options([.. settings]).Value, "1.2.3");
    }

    // ---- options -------------------------------------------------------------------------

    [Fact]
    public void Defaults_AreOff()
    {
        var o = Options().Value; // resolving also runs the validator: off needs no Url
        Assert.False(o.Enabled);
        Assert.False(o.Only);
        Assert.Equal("StudyLife", o.Name);
        Assert.Null(o.Url);
        Assert.False(o.Active);
    }

    [Fact]
    public void Enabled_BindsAllKeys()
    {
        var o = Options(
            ("Discovery:Mdns:Enabled", "true"),
            ("Discovery:Mdns:Url", "https://studylife.example.org"),
            ("Discovery:Mdns:Name", "Home"),
            ("Discovery:Mdns:Only", "true")).Value;
        Assert.True(o.Enabled);
        Assert.True(o.Only);
        Assert.Equal("Home", o.Name);
        Assert.Equal("https://studylife.example.org", o.Url);
    }

    [Fact]
    public void EnabledWithoutUrl_IsRejected()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Options(("Discovery:Mdns:Enabled", "true")).Value);
        Assert.Contains("Discovery:Mdns:Url", ex.Message);
    }

    [Fact]
    public void OnlyWithoutUrl_IsRejected() =>
        Assert.Throws<OptionsValidationException>(() => Options(("Discovery:Mdns:Only", "true")).Value);

    [Theory]
    [InlineData("studylife.example.org")]
    [InlineData("/relative/path")]
    [InlineData("ftp://studylife.example.org")]
    [InlineData("not a url")]
    public void BadUrl_IsRejected(string url) =>
        Assert.Throws<OptionsValidationException>(() =>
            Options(("Discovery:Mdns:Enabled", "true"), ("Discovery:Mdns:Url", url)).Value);

    [Fact]
    public void OverlongName_IsRejected() =>
        Assert.Throws<OptionsValidationException>(() => Options(
            ("Discovery:Mdns:Enabled", "true"),
            ("Discovery:Mdns:Url", "https://a.example"),
            ("Discovery:Mdns:Name", new string('x', 64))).Value);

    // ---- TXT / description ---------------------------------------------------------------

    [Fact]
    public void Https_DefaultsToPort443_AndHasExactlyTheContractedTxtRecords()
    {
        var d = Describe("https://studylife.example.org");
        Assert.Equal(443, d.Port);
        Assert.Equal(["version=1.2.3", "url=https://studylife.example.org", "https=true", "path=/"], d.TxtRecords);
        Assert.Equal("StudyLife", d.InstanceName);
    }

    [Fact]
    public void Http_DefaultsToPort80_AndHttpsFalse()
    {
        var d = Describe("http://studylife.lan");
        Assert.Equal(80, d.Port);
        Assert.Contains("https=false", d.TxtRecords);
        Assert.Contains("url=http://studylife.lan", d.TxtRecords);
    }

    [Fact]
    public void ExplicitPort_IsUsedAndKeptInTheUrl()
    {
        var d = Describe("https://studylife.example.org:8443");
        Assert.Equal(8443, d.Port);
        Assert.Contains("url=https://studylife.example.org:8443", d.TxtRecords);
    }

    [Fact]
    public void TrailingSlashAndPath_AreTrimmedFromTheUrl()
    {
        Assert.Contains("url=https://studylife.example.org", Describe("https://studylife.example.org/").TxtRecords);
        Assert.Contains("url=https://studylife.example.org", Describe("https://studylife.example.org/app/").TxtRecords);
    }

    [Fact]
    public void InstanceName_IsTheConfiguredName() =>
        Assert.Equal("Study Home", Describe("https://a.example", " Study Home ").InstanceName);

    // ---- wire format ---------------------------------------------------------------------

    [Fact]
    public void Announcement_ContainsTheRecordsOfTheContract()
    {
        var packet = MdnsPacket.BuildResponse(Describe("https://studylife.example.org"), [IPAddress.Parse("192.168.1.10")], ttlZero: false);
        var text = System.Text.Encoding.UTF8.GetString(packet);
        Assert.Contains("_studylife", text);
        Assert.Contains("url=https://studylife.example.org", text);
        Assert.Contains("version=1.2.3", text);
        Assert.Contains("https=true", text);
        Assert.Contains("path=/", text);
        Assert.Equal(0x84, packet[2]);
        Assert.Equal(5, packet[7]); // PTR enumeration, PTR type, SRV, TXT, A
        Assert.All(ReadTtls(packet), ttl => Assert.NotEqual(0u, ttl));
    }

    [Fact]
    public void Goodbye_HasTtlZeroOnEveryRecord()
    {
        var packet = MdnsPacket.BuildResponse(Describe("https://studylife.example.org"), [IPAddress.Parse("192.168.1.10")], ttlZero: true);
        var ttls = ReadTtls(packet);
        Assert.Equal(5, ttls.Count);
        Assert.All(ttls, ttl => Assert.Equal(0u, ttl));
    }

    [Fact]
    public void Query_ForTheServiceType_IsReadAndMatched()
    {
        var d = Describe("https://studylife.example.org");
        Assert.True(MdnsPacket.TryReadQuestions(Query(MdnsPacket.TypePtr, "_studylife", "_tcp", "local"), out var questions));
        Assert.Equal("_studylife._tcp.local", Assert.Single(questions).Name);
        Assert.True(MdnsPacket.Matches(d, questions));
    }

    [Fact]
    public void Query_ForSomethingElse_IsNotMatched()
    {
        var d = Describe("https://studylife.example.org");
        Assert.True(MdnsPacket.TryReadQuestions(Query(MdnsPacket.TypePtr, "_http", "_tcp", "local"), out var questions));
        Assert.False(MdnsPacket.Matches(d, questions));
    }

    [Fact]
    public void ResponsesAndGarbage_AreIgnored()
    {
        var d = Describe("https://studylife.example.org");
        Assert.False(MdnsPacket.TryReadQuestions(MdnsPacket.BuildResponse(d, [], false), out _));
        Assert.False(MdnsPacket.TryReadQuestions([1, 2, 3], out _));
        var bad = Query(MdnsPacket.TypePtr, "a");
        Assert.False(MdnsPacket.TryReadQuestions(bad.AsSpan(0, bad.Length - 3), out _));
    }

    private static byte[] Query(ushort type, params string[] labels)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        bytes.AddRange(MdnsPacket.EncodeName(labels));
        bytes.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return bytes.ToArray();
    }

    /// <summary>TTLs of all answer records of an (uncompressed) packet built by MdnsPacket.</summary>
    private static List<uint> ReadTtls(byte[] packet)
    {
        var ttls = new List<uint>();
        var count = (packet[6] << 8) | packet[7];
        var pos = 12;
        for (var i = 0; i < count; i++)
        {
            while (packet[pos] != 0) pos += packet[pos] + 1;
            pos++; // root label
            ttls.Add((uint)((packet[pos + 4] << 24) | (packet[pos + 5] << 16) | (packet[pos + 6] << 8) | packet[pos + 7]));
            var rdLength = (packet[pos + 8] << 8) | packet[pos + 9];
            pos += 10 + rdLength;
        }
        return ttls;
    }

    // ---- hosted service ------------------------------------------------------------------

    [Fact]
    public async Task HostedService_StartsAndWithdrawsTheAnnouncer()
    {
        var fake = new FakeAnnouncer();
        var service = new MdnsHostedService(fake, NullLogger<MdnsHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(0, fake.Stops);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, fake.Stops);
    }

    [Fact]
    public async Task HostedService_AnnouncerFailure_DoesNotCrashTheHost()
    {
        var fake = new FakeAnnouncer(failOnStart: true);
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s =>
            {
                s.AddSingleton<IMdnsAnnouncer>(fake);
                s.AddHostedService<MdnsHostedService>();
            })
            .Build();

        await host.StartAsync();
        await Task.Delay(100);
        Assert.Equal(1, fake.Starts);
        await host.StopAsync(); // must not throw; the host never faulted
    }

    // ---- Only mode ------------------------------------------------------------------------

    [Fact]
    public void IsRequested_ReadsTheCommandLine()
    {
        Assert.True(MdnsOnlyHost.IsRequested(["--Discovery:Mdns:Only=true"]));
        Assert.False(MdnsOnlyHost.IsRequested([]));
    }

    [Fact]
    public async Task OnlyMode_BuildsAHostWithoutDatabaseOrWebServer()
    {
        var fake = new FakeAnnouncer();
        using var host = MdnsOnlyHost.Create(
            ["--Discovery:Mdns:Only=true", "--Discovery:Mdns:Url=https://studylife.example.org"],
            s => s.AddSingleton<IMdnsAnnouncer>(fake));

        Assert.Null(host.Services.GetService<StudyLifeDb>());
        Assert.Null(host.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>());

        await host.StartAsync();
        await Task.Delay(50);
        Assert.Equal(1, fake.Starts);
        await host.StopAsync();
        Assert.Equal(1, fake.Stops);
    }

    [Fact]
    public async Task OnlyMode_WithoutUrl_FailsFast()
    {
        using var host = MdnsOnlyHost.Create(["--Discovery:Mdns:Only=true"], s => s.AddSingleton<IMdnsAnnouncer>(new FakeAnnouncer()));
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
