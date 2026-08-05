using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Step D (Live Activity push): dedicated endpoint (deliberately NOT part of TimerStateDto/
/// Save(), see the TimerStateController comment) + worker phase-change detection
/// (BackgroundTaskService.RunLiveActivityPushAsync). Same stub HTTP pattern as
/// ApnsPushTests, but private there - standalone here because these tests need to feed an
/// actually "enabled" sender into the worker (see
/// BackgroundTaskServiceTestFactory.Create override).
/// </summary>
public class LiveActivityPushEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LiveActivityPushEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string?> ReadStoredTokenAsync()
        => await _factory.WithDbAsync(async db =>
            (await db.TimerState.AsNoTracking().FirstOrDefaultAsync())?.LiveActivityPushToken);

    [Fact]
    public async Task SetLiveActivityPushToken_PersistsToken()
    {
        var response = await _client.PutAsJsonAsync("/api/timerstate/liveactivity-token",
            new LiveActivityPushTokenDto { Token = "activity-token-abc" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("activity-token-abc", await ReadStoredTokenAsync());
    }

    [Fact]
    public async Task SetLiveActivityPushToken_RegularTimerStatePut_DoesNotClobberToken()
    {
        await _client.PutAsJsonAsync("/api/timerstate/liveactivity-token",
            new LiveActivityPushTokenDto { Token = "activity-token-keep-me" });

        // The regular state push (start/pause/stop, TimerService, doesn't know the app-only
        // field) must NOT reset the token to null - exactly the reason for the
        // standalone endpoint instead of a field in TimerStateDto.
        var putResponse = await _client.PutAsJsonAsync("/api/timerstate", new TimerStateDto
        {
            IsRunning = true,
            TimerModeId = 1,
            PhaseEndsAt = DateTime.Now.AddMinutes(25),
        });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        Assert.Equal("activity-token-keep-me", await ReadStoredTokenAsync());
    }

    [Fact]
    public async Task SetLiveActivityPushToken_NullToken_ClearsStoredToken()
    {
        await _client.PutAsJsonAsync("/api/timerstate/liveactivity-token",
            new LiveActivityPushTokenDto { Token = "activity-token-to-clear" });

        var response = await _client.PutAsJsonAsync("/api/timerstate/liveactivity-token",
            new LiveActivityPushTokenDto { Token = null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadStoredTokenAsync());
    }
}

public class LiveActivityPushWorkerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly string _keyPath;

    public LiveActivityPushWorkerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _keyPath = WriteTempP8Key();
    }

    private static string WriteTempP8Key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();
        var path = Path.Combine(Path.GetTempPath(), $"studylife-liveactivity-test-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, pem);
        return path;
    }

    private (BackgroundTaskService Service, StubHttpHandler Handler) CreateService()
    {
        var config = new Dictionary<string, string?>
        {
            ["Apns:KeyPath"] = _keyPath,
            ["Apns:KeyId"] = "TESTKEY123",
            ["Apns:TeamId"] = "TEAM123456",
            ["Apns:BundleId"] = "app.studylife.mobile",
            ["Apns:Endpoint"] = "https://apns.test",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sender = new ApnsSender(configuration, NullLogger<ApnsSender>.Instance, new HttpClient(handler));
        return (BackgroundTaskServiceTestFactory.Create(_factory, sender), handler);
    }

    private async Task SeedTimerStateAsync(Action<TimerStateEntity> configure) =>
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.TimerState.FirstOrDefaultAsync() ?? new TimerStateEntity();
            configure(entity);
            if (entity.Id == 0) db.TimerState.Add(entity);
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task RunLiveActivityPushAsync_NoToken_SendsNothing()
    {
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-5);
            e.LiveActivityPushToken = null;
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_PhaseNotYetDue_SendsNothing()
    {
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddMinutes(10); // still in the future
            e.LiveActivityPushToken = "tok-not-due";
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_NotRunning_SendsNothing()
    {
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = false; // paused/stopped
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-5);
            e.LiveActivityPushToken = "tok-paused";
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_FocusPhaseEnds_SendsUpdateAndAdvancesToBreak()
    {
        // Pomodoro Classic (Id 1): 25 focus / 5 break minutes, 4 rounds.
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-focus-to-break";
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("app.studylife.mobile.push-type.liveactivity", request.Headers["apns-topic"]);
        Assert.Contains("\"event\":\"update\"", request.Body);
        Assert.Contains("\"isBreak\":true", request.Body);
        Assert.Contains("\"round\":1", request.Body);

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstOrDefaultAsync());
        Assert.NotNull(stored);
        Assert.True(stored!.IsBreak);
        Assert.Equal(1, stored.CurrentRound); // the break doesn't increment the round yet
        Assert.True(stored.IsRunning);
        Assert.Equal("tok-focus-to-break", stored.LiveActivityPushToken);
        Assert.True(stored.PhaseEndsAt > DateTime.Now); // advanced to the new (break) phase
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_TransientSendFailure_DoesNotAdvancePhase_RetriesNextTick()
    {
        // An earlier version ALWAYS persisted the new phase state, even on a
        // failed send (Apple's sandbox environment is occasionally slow/
        // unreliable) - the transition was then silently lost, observed live as a
        // frozen card until the FOLLOWING phase expired. PhaseEndsAt must stay in the
        // past so the next tick retries the same transition.
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-transient-fail";
        });
        var config = new Dictionary<string, string?>
        {
            ["Apns:KeyPath"] = _keyPath,
            ["Apns:KeyId"] = "TESTKEY123",
            ["Apns:TeamId"] = "TEAM123456",
            ["Apns:BundleId"] = "app.studylife.mobile",
            ["Apns:Endpoint"] = "https://apns.test",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sender = new ApnsSender(configuration, NullLogger<ApnsSender>.Instance, new HttpClient(handler));
        var service = BackgroundTaskServiceTestFactory.Create(_factory, sender);
        var originalPhaseEndsAt = await _factory.WithDbAsync(async db =>
            (await db.TimerState.AsNoTracking().FirstOrDefaultAsync())!.PhaseEndsAt);

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstOrDefaultAsync());
        Assert.Equal(originalPhaseEndsAt, stored!.PhaseEndsAt); // unchanged, not advanced to the break
        Assert.False(stored.IsBreak); // transition was NOT applied
        Assert.Equal("tok-transient-fail", stored.LiveActivityPushToken); // token stays (not an expired token)
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_MidSessionBreakEnds_AdvancesToNextFocusRound()
    {
        // Break of round 1 (of 4) ends -> next FOCUS phase begins, round counter advances to 2.
        // Complements FocusPhaseEnds (focus -> break) and LastRoundCompletes (final break ->
        // end): the middle state-machine arm "non-final break -> focus" was previously untested.
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = true;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-break-to-focus";
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"event\":\"update\"", request.Body);
        Assert.Contains("\"isBreak\":false", request.Body);
        Assert.Contains("\"round\":2", request.Body);

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstOrDefaultAsync());
        Assert.NotNull(stored);
        Assert.False(stored!.IsBreak);
        Assert.Equal(2, stored.CurrentRound);
        Assert.True(stored.IsRunning);
        Assert.True(stored.PhaseEndsAt > DateTime.Now); // advanced into the new focus phase
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_LastRoundCompletes_SendsEndAndStopsRunning()
    {
        // Last break (round 4 of 4) ends -> session complete, no 5th focus block.
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = true;
            e.CurrentRound = 4;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-session-complete";
        });
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"event\":\"end\"", request.Body);

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstOrDefaultAsync());
        Assert.False(stored!.IsRunning);
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_ExpiredToken_ClearsStoredToken()
    {
        await SeedTimerStateAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-now-invalid";
        });
        var config = new Dictionary<string, string?>
        {
            ["Apns:KeyPath"] = _keyPath,
            ["Apns:KeyId"] = "TESTKEY123",
            ["Apns:TeamId"] = "TEAM123456",
            ["Apns:BundleId"] = "app.studylife.mobile",
            ["Apns:Endpoint"] = "https://apns.test",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var sender = new ApnsSender(configuration, NullLogger<ApnsSender>.Instance, new HttpClient(handler));
        var service = BackgroundTaskServiceTestFactory.Create(_factory, sender);

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstOrDefaultAsync());
        Assert.Null(stored!.LiveActivityPushToken);
    }

    /// <summary>Same stub pattern as ApnsPushTests.StubHttpHandler (private there) - a separate,
    /// deliberately lean copy instead of a cross-file share for a single helper.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(string Uri, Dictionary<string, string> Headers, string Body);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<RecordedRequest> Requests { get; } = [];

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), headers, body));
            return _responder(request);
        }
    }
}
