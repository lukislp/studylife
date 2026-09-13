using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Optimistic concurrency on TimerStateEntity (see its RowVersion doc comment): the one row the
/// API and the worker both write, with the worker holding its read across an outbound APNs call.
/// Its own factory/DB, because these tests deliberately race two DbContexts against the same
/// row - sharing a factory with the other timer suites would make their reads depend on when
/// this one happens to commit.
/// </summary>
public class TimerStateConcurrencyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TimerStateConcurrencyTests(CustomWebApplicationFactory factory) => _factory = factory;

    private async Task<TimerStateEntity> SeedAsync(Action<TimerStateEntity> configure)
    {
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.TimerState.FirstOrDefaultAsync() ?? new TimerStateEntity();
            configure(entity);
            if (entity.Id == 0) db.TimerState.Add(entity);
            await db.SaveChangesAsync();
        });
        return await ReadAsync();
    }

    private async Task<TimerStateEntity> ReadAsync() => await _factory.WithDbAsync(async db =>
        (await db.TimerState.AsNoTracking().FirstAsync()));

    [Fact]
    public async Task SaveChanges_BumpsRowVersion_OnEveryUpdateButNotOnInsert()
    {
        var inserted = await SeedAsync(e => e.TimerModeId = 1);
        var versionAfterInsert = inserted.RowVersion;

        await _factory.WithDbAsync(async db =>
        {
            var row = await db.TimerState.FirstAsync();
            row.CurrentRound = 2;
            await db.SaveChangesAsync();
        });

        var afterUpdate = await ReadAsync();
        Assert.Equal(versionAfterInsert + 1, afterUpdate.RowVersion);
    }

    [Fact]
    public async Task SecondWriterOnAStaleRow_ThrowsDbUpdateConcurrencyException()
    {
        await SeedAsync(e => { e.TimerModeId = 1; e.CurrentRound = 1; });

        using var staleScope = _factory.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var staleRow = await staleDb.TimerState.FirstAsync(); // read BEFORE the other writer

        await _factory.WithDbAsync(async db =>
        {
            var row = await db.TimerState.FirstAsync();
            row.CurrentRound = 42;
            await db.SaveChangesAsync();
        });

        staleRow.CurrentRound = 7;
        // Without the token this would silently overwrite CurrentRound 42 - the whole point.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        Assert.Equal(42, (await ReadAsync()).CurrentRound);
    }

    [Fact]
    public async Task Save_OnConflict_ReloadsAndReappliesTheClientsPut()
    {
        await SeedAsync(e =>
        {
            e.TimerModeId = 1;
            e.IsRunning = false;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.LastClientSequence = null;
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        // Prime this request's DbContext with the row as it stood before the other writer -
        // exactly the state a request that read a moment too early is left holding.
        _ = await db.TimerState.FirstAsync();

        await _factory.WithDbAsync(async other =>
        {
            var row = await other.TimerState.FirstAsync();
            row.IsBreak = true;
            row.CurrentRound = 3;
            await other.SaveChangesAsync();
        });

        var controller = new TimerStateController(
            db,
            scope.ServiceProvider.GetRequiredService<WebhooksProxyClient>(),
            scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>());

        var result = await controller.Save(new TimerStateDto
        {
            IsRunning = false,
            IsBreak = false,
            CurrentRound = 9,
            TimerModeId = 1,
        });

        // The PUT wins (it is the user's own intent), and it landed on the reloaded row rather
        // than being rejected or silently dropped.
        Assert.False(result.IsBreak);
        Assert.Equal(9, result.CurrentRound);
        var stored = await ReadAsync();
        Assert.False(stored.IsBreak);
        Assert.Equal(9, stored.CurrentRound);
    }

    [Fact]
    public async Task SetLiveActivityPushToken_OnConflict_ReloadsAndKeepsTheNewToken()
    {
        await SeedAsync(e => { e.TimerModeId = 1; e.LiveActivityPushToken = "tok-old"; });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        _ = await db.TimerState.FirstAsync();

        // The worker decides the old token is expired and clears it, right as the app registers
        // a new one - the registration must survive, otherwise the live activity is orphaned.
        await _factory.WithDbAsync(async other =>
        {
            var row = await other.TimerState.FirstAsync();
            row.LiveActivityPushToken = null;
            await other.SaveChangesAsync();
        });

        var controller = new TimerStateController(
            db,
            scope.ServiceProvider.GetRequiredService<WebhooksProxyClient>(),
            scope.ServiceProvider.GetRequiredService<ICurrentUserAccessor>());

        var response = await controller.SetLiveActivityPushToken(new LiveActivityPushTokenDto { Token = "tok-fresh" });

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkResult>(response);
        Assert.Equal("tok-fresh", (await ReadAsync()).LiveActivityPushToken);
    }
}

/// <summary>
/// The worker half of the same story: RunLiveActivityPushAsync reads the row, talks to APNs,
/// and only then writes back. A client write landing inside that window must not be overwritten
/// by the phase the worker computed from the older row.
/// </summary>
public class LiveActivityPushConcurrencyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly string _keyPath;

    public LiveActivityPushConcurrencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keyPath = Path.Combine(Path.GetTempPath(), $"studylife-liveactivity-conflict-{Guid.NewGuid():N}.p8");
        File.WriteAllText(_keyPath, key.ExportPkcs8PrivateKeyPem());
    }

    private BackgroundTaskService CreateService(Action onSend)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apns:KeyPath"] = _keyPath,
            ["Apns:KeyId"] = "TESTKEY123",
            ["Apns:TeamId"] = "TEAM123456",
            ["Apns:BundleId"] = "app.studylife.mobile",
            ["Apns:Endpoint"] = "https://apns.test",
        }).Build();
        var sender = new ApnsSender(configuration, NullLogger<ApnsSender>.Instance,
            new HttpClient(new InFlightWriteHandler(onSend)));
        return BackgroundTaskServiceTestFactory.Create(_factory, sender);
    }

    private async Task SeedAsync(Action<TimerStateEntity> configure) =>
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.TimerState.FirstOrDefaultAsync() ?? new TimerStateEntity();
            configure(entity);
            if (entity.Id == 0) db.TimerState.Add(entity);
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task RunLiveActivityPushAsync_RowChangedWhilePushInFlight_KeepsTheClientsState()
    {
        var phaseEndsAt = DateTime.Now.AddSeconds(-2);
        await SeedAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = phaseEndsAt;
            e.LiveActivityPushToken = "tok-in-flight";
        });

        // Runs while the worker is waiting on APNs: the user stops the timer and the app
        // registers a fresh live-activity token.
        var service = CreateService(() =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            var row = db.TimerState.First();
            row.IsRunning = false;
            row.LiveActivityPushToken = "tok-registered-while-in-flight";
            db.SaveChanges();
        });

        await _factory.WithDbAsync(db => service.RunLiveActivityPushAsync(db));

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstAsync());
        Assert.False(stored.IsRunning); // the user's stop stands
        Assert.Equal("tok-registered-while-in-flight", stored.LiveActivityPushToken);
        Assert.False(stored.IsBreak); // the worker's computed transition was NOT applied
        Assert.Equal(1, stored.CurrentRound);
        Assert.Equal(phaseEndsAt, stored.PhaseEndsAt); // next tick recomputes from the fresh row
    }

    [Fact]
    public async Task RunLiveActivityPushAsync_ConflictDoesNotPoisonTheSharedTickDbContext()
    {
        await SeedAsync(e =>
        {
            e.IsRunning = true;
            e.IsBreak = false;
            e.CurrentRound = 1;
            e.TimerModeId = 1;
            e.PhaseEndsAt = DateTime.Now.AddSeconds(-2);
            e.LiveActivityPushToken = "tok-shared-context";
        });

        var service = CreateService(() =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            var row = db.TimerState.First();
            row.CurrentRound = 4;
            db.SaveChanges();
        });

        // ExecuteAsync reuses ONE DbContext for every sub-task of a tick - an unrelated write on
        // that same context afterwards must not replay (and re-throw) the rejected UPDATE.
        await _factory.WithDbAsync(async db =>
        {
            await service.RunLiveActivityPushAsync(db);
            db.Notes.Add(new NoteEntity
            {
                Title = "after the conflict",
                Content = "",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();
        });

        var stored = await _factory.WithDbAsync(async db => await db.TimerState.AsNoTracking().FirstAsync());
        Assert.Equal(4, stored.CurrentRound);
        Assert.False(stored.IsBreak);
    }

    /// <summary>Stub APNs transport that runs <paramref name="onSend"/> - the "concurrent client
    /// write" - at exactly the moment the worker is blocked on the push, which is the window the
    /// concurrency token exists for. Synchronous on purpose: the callback writes via the
    /// blocking EF API, so no waiting on an async result from inside a handler.</summary>
    private sealed class InFlightWriteHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
