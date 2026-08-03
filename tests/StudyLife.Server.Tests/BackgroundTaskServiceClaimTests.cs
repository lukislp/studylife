using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// TryClaimReminderAsync is the claim-first mechanism that protects multiple concurrently
/// running worker replicas (see k8s/05-worker.yaml) from sending the same push reminder
/// twice - the unique index on (AuthUserId, Key) in SentReminderEntity serves as the
/// distributed lock here. Same test pattern as SystemSecretsServiceTests: two independent
/// StudyLifeDb instances on the same temp SQLite file simulate two worker processes that
/// truly compete in parallel (Task.WhenAll) for the same key.
/// </summary>
public class BackgroundTaskServiceClaimTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-claim-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        var db = new StudyLifeDb(options, new TestCurrentUserAccessor());
        db.Database.EnsureCreated();
        // busy_timeout: two connections write to the same file "simultaneously" - without this,
        // SQLite's single-writer limit would show up as a hard SQLITE_BUSY error instead of a
        // serialized wait (Postgres, the real target system for the multi-worker case, does this natively).
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        return db;
    }

    private static BackgroundTaskService NewService() => new(
        services: null!, // TryClaimReminderAsync doesn't use _services
        vapidKeysHolder: new VapidKeysHolder { Keys = new VapidKeys("mailto:test@test", "pub", "priv") },
        logger: NullLogger<BackgroundTaskService>.Instance,
        apnsSender: new ApnsSender(new ConfigurationBuilder().Build(), NullLogger<ApnsSender>.Instance));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TryClaimReminderAsync_TwoConcurrentWorkers_OnlyOneWinsTheSameKey()
    {
        using var dbA = NewContext();
        using var dbB = NewContext();
        var serviceA = NewService();
        var serviceB = NewService();
        var now = DateTime.UtcNow;

        var results = await Task.WhenAll(
            serviceA.TryClaimReminderAsync(dbA, "streakrisk:20260716", now),
            serviceB.TryClaimReminderAsync(dbB, "streakrisk:20260716", now));

        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);

        using var verifyDb = NewContext();
        Assert.Equal(1, await verifyDb.SentReminders.CountAsync(r => r.Key == "streakrisk:20260716"));
    }

    [Fact]
    public async Task TryClaimReminderAsync_DifferentKeys_BothSucceed()
    {
        using var dbA = NewContext();
        using var dbB = NewContext();
        var serviceA = NewService();
        var serviceB = NewService();
        var now = DateTime.UtcNow;

        var results = await Task.WhenAll(
            serviceA.TryClaimReminderAsync(dbA, "streakrisk:20260716", now),
            serviceB.TryClaimReminderAsync(dbB, "weeklygoalnudge:2026-W29", now));

        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task TryClaimReminderAsync_AfterFailedClaim_DoesNotDiscardOtherPendingChanges()
    {
        // Regression test: the catch block in TryClaimReminderAsync must only detach the failed
        // claim entry, not clear the whole change tracker - otherwise, in a loop
        // (e.g. RunCourseAlmostDoneCheckAsync), already-staged, not-yet-saved changes
        // from previous iterations (e.g. removed expired push subscriptions) would be lost.
        using var db = NewContext();
        var service = NewService();
        var now = DateTime.UtcNow;

        Assert.True(await service.TryClaimReminderAsync(db, "coursealmostdone:1:2026-W29", now));

        var sub = new PushSubscriptionEntity { Endpoint = "https://example.test/sub", P256dh = "p", Auth = "a" };
        db.PushSubscriptions.Add(sub);

        // Same key again - must fail this time (already committed), but must not
        // evict the PushSubscription staged above from the change tracker.
        Assert.False(await service.TryClaimReminderAsync(db, "coursealmostdone:1:2026-W29", now));

        await db.SaveChangesAsync();
        using var verifyDb = NewContext();
        Assert.Equal(1, await verifyDb.PushSubscriptions.CountAsync(s => s.Endpoint == "https://example.test/sub"));
    }
}
