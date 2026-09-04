using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using StackExchange.Redis;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

public class ChangeSignalTests
{
    [Fact]
    public async Task InMemory_PublishReachesPerUserAndAllSubscribers_AndDisposeUnsubscribes()
    {
        var signal = new InMemoryChangeSignal();
        var user1 = new List<string>();
        var all = new List<(int, string)>();
        var sub1 = signal.Subscribe(1, k => user1.Add(k));
        using var subAll = signal.SubscribeAll((u, k) => all.Add((u, k)));

        await signal.PublishAsync(1, ChangeKinds.Sessions);
        await signal.PublishAsync(2, ChangeKinds.Settings);
        sub1.Dispose();
        await signal.PublishAsync(1, ChangeKinds.Settings);

        Assert.Equal(["sessions"], user1);
        Assert.Equal([(1, "sessions"), (2, "settings"), (1, "settings")], all);
    }

    [Fact]
    public async Task InMemory_AThrowingHandler_DoesNotStopTheOthers()
    {
        var signal = new InMemoryChangeSignal();
        var reached = false;
        using var bad = signal.Subscribe(1, _ => throw new InvalidOperationException("boom"));
        using var good = signal.Subscribe(1, _ => reached = true);

        await signal.PublishAsync(1, ChangeKinds.Sessions);

        Assert.True(reached);
    }

    [Fact]
    public async Task Redis_PublishesUserAndKind_AndParsesIncomingMessages()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        var signal = new RedisChangeSignal(mux);
        var received = new List<(int, string)>();
        using var sub = signal.SubscribeAll((u, k) => received.Add((u, k)));

        await signal.PublishAsync(42, ChangeKinds.Settings);
        await subscriber.Received(1).PublishAsync(RedisChangeSignal.Channel, (RedisValue)"42:settings", Arg.Any<CommandFlags>());

        signal.OnMessage("7:sessions");
        signal.OnMessage("garbage");
        signal.OnMessage(":sessions");
        Assert.Equal([(7, "sessions")], received);
    }
}

public class SignalInvalidatedVersionCounterTests
{
    private sealed class CountingCounter : IVersionCounter
    {
        public int Reads;
        private readonly Dictionary<string, int> _values = new();
        public Task<int> GetValueAsync(string key) { Reads++; return Task.FromResult(_values.GetValueOrDefault(key)); }
        public Task<int> IncrementAsync(string key) { _values[key] = _values.GetValueOrDefault(key) + 1; return Task.FromResult(_values[key]); }
    }

    [Fact]
    public async Task Reads_AreServedLocally_UntilTheUsersSignalArrives()
    {
        var inner = new CountingCounter();
        var signal = new InMemoryChangeSignal();
        var counter = new SignalInvalidatedVersionCounter(inner, signal, ChangeKinds.Sessions);

        Assert.Equal(0, await counter.GetValueAsync("1"));
        Assert.Equal(0, await counter.GetValueAsync("1"));
        Assert.Equal(1, inner.Reads); // second read answered locally

        await inner.IncrementAsync("1"); // "another pod" wrote...
        Assert.Equal(0, await counter.GetValueAsync("1")); // ...not visible yet (no signal, no TTL expiry)
        await signal.PublishAsync(1, ChangeKinds.Settings); // wrong kind: still local
        Assert.Equal(0, await counter.GetValueAsync("1"));
        await signal.PublishAsync(1, ChangeKinds.Sessions); // right kind: refetched
        Assert.Equal(1, await counter.GetValueAsync("1"));
        Assert.Equal(2, inner.Reads);
    }

    [Fact]
    public async Task Increment_RefreshesTheLocalValue()
    {
        var inner = new CountingCounter();
        var counter = new SignalInvalidatedVersionCounter(inner, new InMemoryChangeSignal(), ChangeKinds.Sessions);

        Assert.Equal(1, await counter.IncrementAsync("1"));
        Assert.Equal(1, await counter.GetValueAsync("1"));
        Assert.Equal(0, inner.Reads);
    }
}

/// <summary>GET /api/events end to end: the stream opens, and a session write by the same user
/// produces a "change" event for "sessions" within a few seconds. Read with
/// ResponseHeadersRead so the never-ending body can be consumed line by line.</summary>
public class EventsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EventsControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Stream_DeliversAChangeEvent_AfterASessionWrite()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType?.ToString());

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal(": connected", await reader.ReadLineAsync(cts.Token));

        var now = DateTime.Now;
        var write = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 31,
            CourseName = "x",
            CourseColor = "#000000",
            StartTime = now.AddDays(5),
            EndTime = now.AddDays(5).AddHours(1),
            IsCompleted = false,
            TimerModeId = 1,
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        string? line;
        var sawEvent = false;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (line == "data: sessions") { sawEvent = true; break; }
        }
        Assert.True(sawEvent, "expected a 'sessions' change event after the write");
    }

    [Fact]
    public async Task StreamV2_CarriesVersions_AndAChangeFrameShowsTheBumpedHistoryVersion()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events?v=2");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("event: state", await reader.ReadLineAsync(cts.Token));
        var connect = ParseFrame(await reader.ReadLineAsync(cts.Token));
        Assert.Null(connect.Kind);

        var now = DateTime.Now;
        var write = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 31,
            CourseName = "x",
            CourseColor = "#000000",
            StartTime = now.AddDays(6),
            EndTime = now.AddDays(6).AddHours(1),
            IsCompleted = false,
            TimerModeId = 1,
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        string? line;
        VersionFrame? change = null;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (line != "event: change") continue;
            change = ParseFrame(await reader.ReadLineAsync(cts.Token));
            break;
        }
        Assert.NotNull(change);
        Assert.Equal("sessions", change!.Kind);
        Assert.Equal(connect.HistoryVersion + 1, change.HistoryVersion);
        Assert.Equal(connect.SettingsVersion, change.SettingsVersion);
    }

    private sealed record VersionFrame(string? Kind, int Seq, int HistoryVersion, int SettingsVersion);

    private static VersionFrame ParseFrame(string? dataLine)
    {
        Assert.NotNull(dataLine);
        Assert.StartsWith("data: ", dataLine);
        return System.Text.Json.JsonSerializer.Deserialize<VersionFrame>(dataLine!["data: ".Length..],
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task StreamV2_BroadcastsEveryWriteKind_NotOnlySessionsAndSettings()
    {
        // The generic ChangeBroadcastFilter: a note write must reach the stream as kind "notes"
        // and move the per-user change sequence, without NotesController knowing about events.
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events?v=2");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("event: state", await reader.ReadLineAsync(cts.Token));
        var connect = ParseFrame(await reader.ReadLineAsync(cts.Token));

        var write = await client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "sse", Content = "broadcast" }, cts.Token);
        Assert.True(write.IsSuccessStatusCode, $"note write failed: {write.StatusCode}");

        string? line;
        VersionFrame? change = null;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (line != "event: change") continue;
            change = ParseFrame(await reader.ReadLineAsync(cts.Token));
            break;
        }
        Assert.NotNull(change);
        Assert.Equal("notes", change!.Kind);
        Assert.Equal(connect.Seq + 1, change.Seq);
        Assert.Equal(connect.HistoryVersion, change.HistoryVersion);
        Assert.Equal(connect.SettingsVersion, change.SettingsVersion);
    }

    [Fact]
    public void KindFromPath_TakesTheFirstSegmentAfterApi()
    {
        Assert.Equal("notes", ChangeBroadcastFilter.KindFromPath("/api/notes/5"));
        Assert.Equal("coursegoals", ChangeBroadcastFilter.KindFromPath("/api/coursegoals"));
        Assert.Equal("settings", ChangeBroadcastFilter.KindFromPath("/api/settings/ha-api-key/generate"));
        Assert.Null(ChangeBroadcastFilter.KindFromPath("/healthz"));
    }

    [Fact]
    public async Task Stream_RequiresASession()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/events")).StatusCode);
    }
}
