// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>Every retention rule of DataRetentionCleaner (GDPR audit F-15): old rows go, young or
/// still-needed rows stay. Each test uses its own user id so the shared factory DB does not leak.</summary>
public class DataRetentionCleanerTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly CustomWebApplicationFactory _factory;

    public DataRetentionCleanerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
    }

    private async Task<DataRetentionCleaner.Result> RunAsync(Func<StudyLifeDb, Task> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await seed(db);
        await db.SaveChangesAsync();
        return await new DataRetentionCleaner(new RetentionOptions()).RunAsync(db, Now);
    }

    private async Task<T> ReadAsync<T>(Func<StudyLifeDb, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }

    [Fact]
    public async Task ExpiredSessions_AreDeleted_LiveOnesStay()
    {
        await RunAsync(db =>
        {
            db.AuthSessions.Add(new AuthSessionEntity { AuthUserId = 9101, TokenHash = "ret-sess-expired", IssuedAt = Now.AddDays(-100), ExpiresAt = Now.AddDays(-1), HardExpiresAt = Now.AddDays(80), LastUsedAt = Now.AddDays(-91) });
            db.AuthSessions.Add(new AuthSessionEntity { AuthUserId = 9101, TokenHash = "ret-sess-hard", IssuedAt = Now.AddDays(-181), ExpiresAt = Now.AddDays(10), HardExpiresAt = Now.AddHours(-1), LastUsedAt = Now.AddDays(-1) });
            db.AuthSessions.Add(new AuthSessionEntity { AuthUserId = 9101, TokenHash = "ret-sess-live", IssuedAt = Now.AddDays(-5), ExpiresAt = Now.AddDays(80), HardExpiresAt = Now.AddDays(170), LastUsedAt = Now });
            return Task.CompletedTask;
        });

        var hashes = await ReadAsync(db => db.AuthSessions.Where(s => s.AuthUserId == 9101).Select(s => s.TokenHash).ToListAsync());
        Assert.Equal(["ret-sess-live"], hashes);
    }

    [Fact]
    public async Task Invites_UsedOrExpiredMoreThan30DaysAgo_AreDeleted()
    {
        await RunAsync(db =>
        {
            db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-inv-used-old", CreatedByUserId = 9102, CreatedAt = Now.AddDays(-60), ExpiresAt = Now.AddDays(-53), UsedAt = Now.AddDays(-31), UsedByUserId = 9103 });
            db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-inv-expired-old", CreatedByUserId = 9102, CreatedAt = Now.AddDays(-60), ExpiresAt = Now.AddDays(-31) });
            db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-inv-used-recent", CreatedByUserId = 9102, CreatedAt = Now.AddDays(-10), ExpiresAt = Now.AddDays(-3), UsedAt = Now.AddDays(-5), UsedByUserId = 9104 });
            db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-inv-expired-recent", CreatedByUserId = 9102, CreatedAt = Now.AddDays(-20), ExpiresAt = Now.AddDays(-13) });
            db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-inv-open", CreatedByUserId = 9102, CreatedAt = Now, ExpiresAt = Now.AddDays(7) });
            return Task.CompletedTask;
        });

        var hashes = await ReadAsync(db => db.AuthInvites.Where(i => i.CreatedByUserId == 9102).Select(i => i.TokenHash).OrderBy(h => h).ToListAsync());
        Assert.Equal(["ret-inv-expired-recent", "ret-inv-open", "ret-inv-used-recent"], hashes);
    }

    [Fact]
    public async Task RecoveryCodes_SpentMoreThan30DaysAgo_AreDeleted_UnusedAndRecentStay()
    {
        await RunAsync(db =>
        {
            db.RecoveryCodes.Add(new RecoveryCodeEntity { AuthUserId = 9105, CodeHash = "ret-rc-used-old", CreatedAt = Now.AddDays(-90), UsedAt = Now.AddDays(-31) });
            db.RecoveryCodes.Add(new RecoveryCodeEntity { AuthUserId = 9105, CodeHash = "ret-rc-used-recent", CreatedAt = Now.AddDays(-90), UsedAt = Now.AddDays(-2) });
            db.RecoveryCodes.Add(new RecoveryCodeEntity { AuthUserId = 9105, CodeHash = "ret-rc-unused", CreatedAt = Now.AddDays(-400) });
            return Task.CompletedTask;
        });

        var hashes = await ReadAsync(db => db.RecoveryCodes.Where(c => c.AuthUserId == 9105).Select(c => c.CodeHash).OrderBy(h => h).ToListAsync());
        Assert.Equal(["ret-rc-unused", "ret-rc-used-recent"], hashes);
    }

    [Fact]
    public async Task Passkeys_UnapprovedForMoreThan7Days_AreDeleted_ApprovedAndFreshStay()
    {
        await RunAsync(db =>
        {
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity { AuthUserId = 9106, CredentialId = [1, 9, 1], CreatedAt = Now.AddDays(-8), ApprovedAt = null });
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity { AuthUserId = 9106, CredentialId = [1, 9, 2], CreatedAt = Now.AddDays(-2), ApprovedAt = null });
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity { AuthUserId = 9106, CredentialId = [1, 9, 3], CreatedAt = Now.AddDays(-300), ApprovedAt = Now.AddDays(-299) });
            return Task.CompletedTask;
        });

        var ids = await ReadAsync(async db => (await db.PasskeyCredentials.Where(p => p.AuthUserId == 9106).ToListAsync())
            .Select(p => p.CredentialId[2]).Order().ToList());
        Assert.Equal([(byte)2, (byte)3], ids);
    }

    [Fact]
    public async Task PushSubscriptions_OldAndOwnerInactive_AreDeleted_ActiveOwnerKeepsThem()
    {
        await RunAsync(db =>
        {
            // 9107: old subscription, only a session unused for > 180 days -> gone
            db.PushSubscriptions.Add(new PushSubscriptionEntity { AuthUserId = 9107, Endpoint = "https://push.example/ret/inactive", CreatedAt = Now.AddDays(-200) });
            db.AuthSessions.Add(new AuthSessionEntity { AuthUserId = 9107, TokenHash = "ret-push-old-sess", IssuedAt = Now.AddDays(-250), ExpiresAt = Now.AddDays(1), HardExpiresAt = Now.AddDays(1), LastUsedAt = Now.AddDays(-181) });
            // 9108: old subscription, but the owner used a session yesterday -> stays
            db.PushSubscriptions.Add(new PushSubscriptionEntity { AuthUserId = 9108, Endpoint = "https://push.example/ret/active", CreatedAt = Now.AddDays(-200) });
            db.AuthSessions.Add(new AuthSessionEntity { AuthUserId = 9108, TokenHash = "ret-push-new-sess", IssuedAt = Now.AddDays(-20), ExpiresAt = Now.AddDays(80), HardExpiresAt = Now.AddDays(160), LastUsedAt = Now.AddDays(-1) });
            // 9109: recent subscription, no session at all -> stays (younger than the cutoff)
            db.PushSubscriptions.Add(new PushSubscriptionEntity { AuthUserId = 9109, Endpoint = "https://push.example/ret/new", CreatedAt = Now.AddDays(-3) });
            // 9110: legacy row without CreatedAt and no session -> gone
            db.PushSubscriptions.Add(new PushSubscriptionEntity { AuthUserId = 9110, Endpoint = "https://push.example/ret/legacy", CreatedAt = null });
            return Task.CompletedTask;
        });

        var endpoints = await ReadAsync(db => db.PushSubscriptions.IgnoreQueryFilters()
            .Where(p => p.AuthUserId >= 9107 && p.AuthUserId <= 9110).Select(p => p.Endpoint).OrderBy(e => e).ToListAsync());
        Assert.Equal(["https://push.example/ret/active", "https://push.example/ret/new"], endpoints);
    }

    [Fact]
    public async Task AchievementMarkers_OfUnknownCategories_AreDeleted_KnownOnesStay()
    {
        await RunAsync(db =>
        {
            db.SentReminders.Add(new SentReminderEntity { AuthUserId = 9111, Key = "achievement:hours:100", SentAt = Now.AddDays(-400) });
            db.SentReminders.Add(new SentReminderEntity { AuthUserId = 9111, Key = "achievement:allcourses", SentAt = Now.AddDays(-400) });
            db.SentReminders.Add(new SentReminderEntity { AuthUserId = 9111, Key = "achievement:retired:5", SentAt = Now.AddDays(-400) });
            db.SentReminders.Add(new SentReminderEntity { AuthUserId = 9111, Key = "coursegoal:7:reminder3d", SentAt = Now.AddDays(-400) });
            return Task.CompletedTask;
        });

        var keys = await ReadAsync(db => db.SentReminders.IgnoreQueryFilters()
            .Where(r => r.AuthUserId == 9111).Select(r => r.Key).OrderBy(k => k).ToListAsync());
        Assert.Equal(["achievement:allcourses", "achievement:hours:100", "coursegoal:7:reminder3d"], keys);
    }

    [Fact]
    public async Task RetentionValues_AreConfigurable()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        db.AuthInvites.Add(new AuthInviteEntity { TokenHash = "ret-cfg-inv", CreatedByUserId = 9112, CreatedAt = Now.AddDays(-20), ExpiresAt = Now.AddDays(-13) });
        await db.SaveChangesAsync();

        await new DataRetentionCleaner(new RetentionOptions { InviteDays = 10 }).RunAsync(db, Now);

        Assert.False(await db.AuthInvites.AnyAsync(i => i.TokenHash == "ret-cfg-inv"));
    }

    [Fact]
    public async Task WorkerRun_UsesTheRegisteredCleaner_WithoutThrowing()
    {
        using var scope = _factory.Services.CreateScope();
        await BackgroundTaskServiceTestFactory.Create(_factory)
            .RunDataRetentionAsync(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }
}
