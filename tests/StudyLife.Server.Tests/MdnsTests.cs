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
        public List<string> Ids { get; } = [];

        public void SetId(string id) => Ids.Add(id);

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
            s => { s.AddSingleton<IMdnsAnnouncer>(fake); s.AddSingleton<IMdnsIdSource>(new ScriptedIdSource()); });

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
        using var host = MdnsOnlyHost.Create(["--Discovery:Mdns:Only=true"], s => { s.AddSingleton<IMdnsAnnouncer>(new FakeAnnouncer()); s.AddSingleton<IMdnsIdSource>(new ScriptedIdSource()); });
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    // ---- instance id: options -------------------------------------------------------------

    private const string SampleId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void InstanceUrlAndId_BindAndDefaultToUrlAndNull()
    {
        var defaults = Options(("Discovery:Mdns:Only", "true"), ("Discovery:Mdns:Url", "https://studylife.example.org")).Value;
        Assert.Null(defaults.InstanceUrl);
        Assert.Null(defaults.Id);
        Assert.Equal("https://studylife.example.org/", defaults.ParsedInstanceUrl!.AbsoluteUri);

        var o = Options(
            ("Discovery:Mdns:Only", "true"),
            ("Discovery:Mdns:Url", "https://studylife.example.org"),
            ("Discovery:Mdns:InstanceUrl", "http://studylife-web.studylife.svc:8080"),
            ("Discovery:Mdns:Id", SampleId.ToUpperInvariant())).Value;
        Assert.Equal("http://studylife-web.studylife.svc:8080/", o.ParsedInstanceUrl!.AbsoluteUri);
        Assert.Equal(SampleId, o.ParsedId); // normalised to lowercase
    }

    [Theory]
    [InlineData("studylife-web")]
    [InlineData("ftp://studylife-web")]
    public void BadInstanceUrl_IsRejected(string url)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Options(
            ("Discovery:Mdns:Only", "true"), ("Discovery:Mdns:Url", "https://a.example"), ("Discovery:Mdns:InstanceUrl", url)).Value);
        Assert.Contains("Discovery:Mdns:InstanceUrl", ex.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void BadId_IsRejected(string id)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Options(
            ("Discovery:Mdns:Only", "true"), ("Discovery:Mdns:Url", "https://a.example"), ("Discovery:Mdns:Id", id)).Value);
        Assert.Contains("Discovery:Mdns:Id", ex.Message);
    }

    [Fact]
    public void InstanceUrlAndId_AreNotValidatedWhileTheFeatureIsOff() =>
        Assert.Null(Options(("Discovery:Mdns:InstanceUrl", "nonsense"), ("Discovery:Mdns:Id", "x")).Value.ParsedId);

    // ---- instance id: TXT ---------------------------------------------------------------

    [Fact]
    public void Txt_IncludesTheIdWhenGiven_AndOmitsItWhenNull()
    {
        var options = Options(("Discovery:Mdns:Enabled", "true"), ("Discovery:Mdns:Url", "https://studylife.example.org")).Value;

        var withId = MdnsServiceDescription.From(options, "1.2.3", SampleId);
        Assert.Equal(["version=1.2.3", "url=https://studylife.example.org", "https=true", "path=/", $"id={SampleId}"], withId.TxtRecords);
        Assert.Equal(SampleId, withId.Id);

        var without = MdnsServiceDescription.From(options, "1.2.3", null);
        Assert.DoesNotContain(without.TxtRecords, t => t.StartsWith("id=", StringComparison.Ordinal));
        Assert.Null(without.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdez")]
    public void Txt_IgnoresAMalformedId(string id)
    {
        var options = Options(("Discovery:Mdns:Enabled", "true"), ("Discovery:Mdns:Url", "https://studylife.example.org")).Value;
        var d = MdnsServiceDescription.From(options, "1.2.3", id);
        Assert.Equal(4, d.TxtRecords.Count);
        Assert.Same(d, d.WithId(id));
    }

    [Fact]
    public void WithId_AddsOrReplacesTheIdRecord()
    {
        var d = Describe("https://studylife.example.org");
        var first = d.WithId(SampleId);
        var second = first.WithId("fedcba9876543210fedcba9876543210");
        Assert.Single(second.TxtRecords, t => t.StartsWith("id=", StringComparison.Ordinal));
        Assert.Equal("fedcba9876543210fedcba9876543210", second.Id);
        Assert.Null(d.Id); // the original is untouched
    }

    [Fact]
    public void Announcement_ContainsTheIdRecord()
    {
        var packet = MdnsPacket.BuildResponse(Describe("https://studylife.example.org").WithId(SampleId), [IPAddress.Parse("192.168.1.10")], ttlZero: false);
        Assert.Contains($"id={SampleId}", System.Text.Encoding.UTF8.GetString(packet));
    }

    // ---- instance id: HTTP source (announcer-only mode) ------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpMdnsIdSource HttpSource(FakeHandler handler, string url = "https://studylife.example.org/some/path") =>
        new(new HttpClient(handler), new Uri(url), NullLogger<HttpMdnsIdSource>.Instance);

    [Fact]
    public async Task HttpSource_FetchesTheIdFromApiInstance()
    {
        var handler = new FakeHandler(_ => Json($"{{\"id\":\"{SampleId}\",\"version\":\"1.2.3\"}}"));

        var id = await HttpSource(handler).TryGetIdAsync(CancellationToken.None);

        Assert.Equal(SampleId, id);
        Assert.Equal("https://studylife.example.org/api/instance", Assert.Single(handler.Requests).AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"id\":\"nope\"}")]
    [InlineData(HttpStatusCode.OK, "{\"version\":\"1\"}")]
    public async Task HttpSource_BadResponses_YieldNullWithoutThrowing(HttpStatusCode status, string body)
    {
        var id = await HttpSource(new FakeHandler(_ => Json(body, status))).TryGetIdAsync(CancellationToken.None);
        Assert.Null(id);
    }

    [Fact]
    public async Task HttpSource_ConnectionFailure_YieldsNull()
    {
        var id = await HttpSource(new FakeHandler(_ => throw new HttpRequestException("connection refused"))).TryGetIdAsync(CancellationToken.None);
        Assert.Null(id);
    }

    [Fact]
    public async Task HttpSource_ExplicitCancellation_Propagates() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpSource(new FakeHandler(_ => throw new TaskCanceledException()))
                .TryGetIdAsync(new CancellationToken(canceled: true)));

    // ---- instance id: hosted service ------------------------------------------------------

    private sealed class ScriptedIdSource(params string?[] results) : IMdnsIdSource
    {
        private int _calls;
        public int Calls => _calls;

        public Task<string?> TryGetIdAsync(CancellationToken cancellationToken)
        {
            var i = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(results.Length == 0 ? null : results[Math.Min(i, results.Length - 1)]);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task HostedService_WithAnAvailableId_AnnouncesItBeforeStarting()
    {
        var fake = new FakeAnnouncer();
        var source = new ScriptedIdSource(SampleId);
        var service = new MdnsHostedService(fake, NullLogger<MdnsHostedService>.Instance, source, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fake.Starts == 1);

        Assert.Equal([SampleId], fake.Ids);
        await Task.Delay(60);
        Assert.Equal(1, source.Calls); // no retry once the id is known
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedService_FailedFetch_AnnouncesWithoutId_ThenRetriesAndAnnouncesWithIt()
    {
        var fake = new FakeAnnouncer();
        var source = new ScriptedIdSource(null, null, SampleId);
        var service = new MdnsHostedService(fake, NullLogger<MdnsHostedService>.Instance, source, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fake.Starts == 1);

        await WaitUntilAsync(() => fake.Ids.Count == 1);
        Assert.Equal([SampleId], fake.Ids);
        Assert.Equal(1, fake.Starts); // announced once, then re-announced via SetId - never restarted
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedService_ThrowingSource_DoesNotCrashAndStillAnnounces()
    {
        var fake = new FakeAnnouncer();
        var service = new MdnsHostedService(fake, NullLogger<MdnsHostedService>.Instance, new ThrowingIdSource(), TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fake.Starts == 1);

        Assert.Empty(fake.Ids);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class ThrowingIdSource : IMdnsIdSource
    {
        public Task<string?> TryGetIdAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Registration_OnlyModeWithExplicitId_SkipsTheHttpFetch()
    {
        var fake = new FakeAnnouncer();
        using var host = MdnsOnlyHost.Create(
            ["--Discovery:Mdns:Only=true", "--Discovery:Mdns:Url=https://studylife.example.org", $"--Discovery:Mdns:Id={SampleId}"],
            s => s.AddSingleton<IMdnsAnnouncer>(fake));

        Assert.IsType<FixedMdnsIdSource>(host.Services.GetRequiredService<IMdnsIdSource>());
        await host.StartAsync();
        await WaitUntilAsync(() => fake.Starts == 1);
        Assert.Equal([SampleId], fake.Ids);
        await host.StopAsync();
    }

    [Fact]
    public void Registration_OnlyModeWithoutId_UsesTheHttpSourceOnInstanceUrl()
    {
        using var host = MdnsOnlyHost.Create(
            ["--Discovery:Mdns:Only=true", "--Discovery:Mdns:Url=https://studylife.example.org", "--Discovery:Mdns:InstanceUrl=http://web.svc:8080"],
            s => s.AddSingleton<IMdnsAnnouncer>(new FakeAnnouncer()));

        Assert.IsType<HttpMdnsIdSource>(host.Services.GetRequiredService<IMdnsIdSource>());
    }

    private sealed class FakeProvider(Func<Task<string>> get) : StudyLife.Server.Services.IInstanceIdProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => get();
    }

    [Fact]
    public async Task ProviderSource_ReturnsThePersistedId_AndNullWhenTheDatabaseIsNotReadable()
    {
        var ok = new ProviderMdnsIdSource(new FakeProvider(() => Task.FromResult(SampleId)), NullLogger<ProviderMdnsIdSource>.Instance);
        var broken = new ProviderMdnsIdSource(new FakeProvider(() => throw new InvalidOperationException("db down")), NullLogger<ProviderMdnsIdSource>.Instance);

        Assert.Equal(SampleId, await ok.TryGetIdAsync(CancellationToken.None));
        Assert.Null(await broken.TryGetIdAsync(CancellationToken.None));
    }
}
