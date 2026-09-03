using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Builds an actually ENABLED ApnsSender whose HTTP traffic goes against a stub handler with a
/// fixed status code - the counterpart to the DI-registered sender of the test host, which has
/// no Apns:* config and is therefore always Enabled=false. Needed by all tests that want to
/// drive SendPushAsync's APNs branch (Delivered/ExpiredToken) without network access. Same
/// p8/config pattern as LiveActivityPushWorkerTests, homed here because several
/// BackgroundTaskService test files share it.
/// </summary>
internal static class ApnsStubSender
{
    public static ApnsSender Create(HttpStatusCode status)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(Path.GetTempPath(), $"studylife-apns-stub-{Guid.NewGuid():N}.p8");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apns:KeyPath"] = keyPath,
            ["Apns:KeyId"] = "TESTKEY123",
            ["Apns:TeamId"] = "TEAM123456",
            ["Apns:BundleId"] = "app.studylife.mobile",
            ["Apns:Endpoint"] = "https://apns.test",
        }).Build();
        return new ApnsSender(configuration, NullLogger<ApnsSender>.Instance,
            new HttpClient(new FixedStatusHandler(status)));
    }

    private sealed class FixedStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public FixedStatusHandler(HttpStatusCode status) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status));
    }
}

/// <summary>
/// Inserts an APNs-channel push subscription directly (there is no dedicated APNs subscribe
/// HTTP path in the test host) - Endpoint uses the synthetic "apns:&lt;token&gt;" convention from
/// PushSubscriptionEntity, AuthUserId is stamped by StudyLifeDb.SaveChanges via the ambient
/// fallback user (see CustomWebApplicationFactory).
/// </summary>
internal static class ApnsSubscriptionSeeder
{
    public static Task SeedAsync(CustomWebApplicationFactory factory, string token, string? apnsToken = null) =>
        factory.WithDbAsync(async db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Channel = PushSubscriptionEntity.ChannelApns,
                ApnsToken = apnsToken ?? token,
                Endpoint = $"apns:{token}",
            });
            await db.SaveChangesAsync();
        });
}

/// <summary>
/// Counterpart to GoneEndpoint (see BackgroundTaskServiceTestHelpers): answers every request
/// with 201 Created, the way a real push service acknowledges an accepted web push - the only
/// way to drive SendPushAsync's SUCCESS return (WebPushClient needs a real HTTP endpoint plus
/// cryptographically valid keys, see FakePushKeys).
/// </summary>
internal sealed class CreatedEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    public string Url { get; }

    /// <summary>Requests served so far - lets delivery tests assert the push actually
    /// reached the endpoint, not just that no exception surfaced.</summary>
    public int RequestCount => _requestCount;
    private int _requestCount;

    public CreatedEndpoint()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Url = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = ServeLoopAsync();
    }

    private async Task ServeLoopAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                Interlocked.Increment(ref _requestCount);
                ctx.Response.StatusCode = 201;
                ctx.Response.Close();
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { /* best effort */ }
        try { _listener.Close(); } catch (Exception) { /* best effort */ }
    }
}

/// <summary>
/// Covers the ExecuteAsync dispatch loop itself (scope creation, subscriptions memoization,
/// the gate booleans and their finally blocks). All _next*Run fields start at DateTime.MinValue,
/// so the very first tick invariably runs all nine Run*Async methods - that makes a single
/// awaited tick meaningful, without having to wait out the real 30s interval. Own factory/DB,
/// because a real VACUUM and backup dump operation runs here.
/// </summary>
public class BackgroundTaskServiceExecuteAsyncTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceExecuteAsyncTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task StartAsync_FirstTick_RunsAllGatedSubTasksImmediately()
    {
        // Deliberately a syntactically invalid endpoint (instead of a valid but unreachable
        // one) - fails immediately without network access (see
        // BackgroundTaskServicePushNotificationTests), so that this integration test doesn't
        // depend on network timeouts.
        // Inserted directly: PushController.Subscribe now rejects non-https endpoints
        // (OutboundUrlPolicy), so the deliberately broken endpoint has to bypass the API.
        await _factory.WithDbAsync(db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                AuthUserId = 1, Endpoint = "this is not a url", P256dh = "p256dh-key-value", Auth = "auth-key-value", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChangesAsync();
        });

        var service = BackgroundTaskServiceTestFactory.Create(_factory);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        try
        {
            // No sessions/course-goal record present -> RunInactivityReminderCheckAsync fires
            // ("never studied yet") and is thus a reliable signal that the first tick has run,
            // without having to query any internal state of the service.
            List<SentReminderEntity> inactivityRows = new();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
                inactivityRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync();
                if (inactivityRows.Count > 0) break;
                await Task.Delay(100);
            }
            Assert.NotEmpty(inactivityRows);

            // RunBackupDumpAsync + RunDatabaseMaintenanceAsync also already run in the first
            // tick (_nextBackupDumpRun/_nextDatabaseMaintenanceRun start at DateTime.MinValue).
            var backupDir = Path.Combine(_factory.BackupContentRoot, "app_data", "backups");
            string[] files = Array.Empty<string>();
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (Directory.Exists(backupDir))
                {
                    files = Directory.GetFiles(backupDir, "studylife-*.db");
                    if (files.Length > 0) break;
                }
                await Task.Delay(100);
            }
            Assert.NotEmpty(files);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// The OperationCanceledException catch in ExecuteAsync's user-list block: a cancellation
/// surfacing INSIDE the try (here via the shard claim, the last awaited call of the block) must
/// break out of the while loop and let ExecuteAsync complete normally - not fault the
/// BackgroundService task.
/// </summary>
public class BackgroundTaskServiceCancelledClaimTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public BackgroundTaskServiceCancelledClaimTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient(); // host (incl. migration + VAPID keys) must be up before construction
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringClaim_BreaksLoop_AndCompletesCleanly()
    {
        var claim = new CancelledShardClaim();
        var service = new BackgroundTaskService(
            _factory.Services,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            _factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
            _factory.Services.GetRequiredService<ApnsSender>(),
            claim);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var executeTask = service.ExecuteTask!;
            var finished = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.Same(executeTask, finished);
            Assert.True(executeTask.IsCompletedSuccessfully,
                $"ExecuteAsync must end via break, not fault: {executeTask.Exception}");
            Assert.True(claim.Called);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CancelledShardClaim : IWorkerShardClaim
    {
        public bool Called { get; private set; }
        public int LastReplicaCount => 1;
        public Task<int?> ClaimOrRenewAsync(CancellationToken ct)
        {
            Called = true;
            throw new OperationCanceledException();
        }
    }
}

/// <summary>
/// The per-task catch blocks in ExecuteAsync: if EVERY dependency of a tick fails (per-user DB
/// scope broken, maintenance DB broken, backup source file missing), each sub-task must be
/// caught individually and the loop must reach the next tick regardless - the design promise
/// "don't let the whole background loop die" from the production comments. The wrapped service
/// provider serves the user-LIST scope (first scope) from the real container so the loop finds
/// the seeded AuthUser, and every later scope (per-user + maintenance) with a StudyLifeDb whose
/// SQLite file sits in a nonexistent directory - every query throws immediately, without
/// network/timeouts. The second tick's user-list load then ALSO fails, which drives the outer
/// "Error loading the AuthUser list" catch.
/// </summary>
public class BackgroundTaskServiceFaultToleranceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public BackgroundTaskServiceFaultToleranceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
    }

    [Fact]
    public async Task ExecuteAsync_AllDependenciesFailing_EveryCatchSwallows_AndLoopReachesNextTick()
    {
        var provider = new FirstScopeRealThenBrokenProvider(_factory.Services);
        // Backup source in a nonexistent directory -> CreateWeeklyBackup throws on source open,
        // which must land in the RunBackupDumpAsync catch (content root is a separate temp dir
        // so the created app_data/backups skeleton never resurrects the missing db directory).
        var missingRoot = Path.Combine(Path.GetTempPath(), $"studylife-missing-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"studylife-broken-backup-{Guid.NewGuid():N}");
        var backup = new DatabaseBackupService(Path.Combine(missingRoot, "never-created.db"), backupRoot);
        var service = new BackgroundTaskService(
            provider,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            _factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
            // Enabled sender, so RunLiveActivityPushAsync gets past its Enabled gate and its DB
            // access lands in the LiveActivityPushService catch as well.
            ApnsStubSender.Create(HttpStatusCode.OK),
            shardClaim: null,
            backupService: backup);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Scope sequence: #1 user list (real), #2 user 1 (broken), #3 maintenance (broken),
            // then after the tick delay #4 = the SECOND tick's user-list scope (broken).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (provider.ScopeCount < 4 && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.True(provider.ScopeCount >= 4,
                $"the loop must survive the fully failing first tick and start a second one (scopes: {provider.ScopeCount})");

            // Give the second tick's user-list catch a moment to complete, then verify the
            // loop is still alive (parked in Task.Delay) instead of faulted.
            await Task.Delay(300);
            Assert.False(service.ExecuteTask!.IsCompleted,
                $"loop must keep running after swallowed failures: {service.ExecuteTask.Exception}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            try { Directory.Delete(backupRoot, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    private sealed class FirstScopeRealThenBrokenProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly IServiceProvider _real;
        private int _scopeCount;

        public FirstScopeRealThenBrokenProvider(IServiceProvider real) => _real = real;

        public int ScopeCount => Volatile.Read(ref _scopeCount);

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : _real.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            var n = Interlocked.Increment(ref _scopeCount);
            return n == 1
                ? _real.GetRequiredService<IServiceScopeFactory>().CreateScope()
                : new BrokenScope();
        }
    }

    /// <summary>Scope whose StudyLifeDb resolves fine (resolution happens OUTSIDE the per-task
    /// try blocks) but whose every query throws, because the SQLite file lives in a directory
    /// that doesn't exist.</summary>
    private sealed class BrokenScope : IServiceScope, IServiceProvider
    {
        private readonly StudyLifeDb _db;

        public BrokenScope()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"studylife-broken-{Guid.NewGuid():N}", "broken.db");
            var options = new DbContextOptionsBuilder<StudyLifeDb>()
                .UseSqlite($"Data Source={missing}")
                .Options;
            _db = new StudyLifeDb(options, new CurrentUserAccessor(new HttpContextAccessor()));
        }

        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(StudyLifeDb) ? _db : null;
        public void Dispose() => _db.Dispose();
    }
}

/// <summary>
/// SendPushAsync's APNs branch with a DISABLED sender (the free-tier default): the send must be
/// a silent no-op - subscription stays, the reminder is still recorded (claim-first). Driven via
/// the inactivity check, the cheapest reliably firing sub-task ("never studied" fires
/// immediately).
/// </summary>
public class BackgroundTaskServiceApnsChannelDisabledSenderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceApnsChannelDisabledSenderTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        // DI-registered sender of the test host: no Apns:* config -> Enabled=false.
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ApnsSubscription_DisabledSender_IsSilentNoOp_SubscriptionStays()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.InactivityRemindersEnabled = true);
        await ApnsSubscriptionSeeder.SeedAsync(_factory, "tok-disabled-sender");

        await _factory.WithDbAsync(db => _service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync()));
        Assert.Single(await _factory.WithDbAsync(db =>
            db.PushSubscriptions.AsNoTracking().ToListAsync()));
    }
}

/// <summary>
/// SendPushAsync's APNs branch with an ENABLED sender: a delivered push (200) must keep the
/// subscription, and a row without a device token (defensive: ApnsToken null despite
/// channel "apns") must be skipped without a send attempt.
/// </summary>
public class BackgroundTaskServiceApnsChannelDeliveredTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceApnsChannelDeliveredTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ApnsSubscriptions_DeliveredAndTokenless_BothSurvive()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.InactivityRemindersEnabled = true);
        await ApnsSubscriptionSeeder.SeedAsync(_factory, "tok-delivered");
        await _factory.WithDbAsync(async db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Channel = PushSubscriptionEntity.ChannelApns,
                ApnsToken = null, // legacy/inconsistent row - must be skipped, not crash
                Endpoint = "apns:tokenless",
            });
            await db.SaveChangesAsync();
        });
        var service = BackgroundTaskServiceTestFactory.Create(_factory, ApnsStubSender.Create(HttpStatusCode.OK));

        await _factory.WithDbAsync(db => service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync()));
        // Neither the delivered nor the tokenless subscription may be removed.
        Assert.Equal(2, (await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().ToListAsync())).Count);
    }
}

/// <summary>
/// SendPushAsync's web-push SUCCESS return: a real local endpoint acknowledges with 201 (like a
/// production push service), the payload encryption uses cryptographically valid keys
/// (FakePushKeys) - subscription stays, reminder recorded. All existing web push tests only
/// exercised the failure paths (encryption error or 410 Gone).
/// </summary>
public class BackgroundTaskServiceWebPushDeliveredTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWebPushDeliveredTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task WebPushSubscription_EndpointAccepts_SubscriptionStays_ReminderRecorded()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.InactivityRemindersEnabled = true);
        using var accepted = new CreatedEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, accepted.Url, p256dh, auth);

        await _factory.WithDbAsync(db => _service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync()));
        Assert.Single(await _factory.WithDbAsync(db =>
            db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == accepted.Url).ToListAsync()));
    }
}
