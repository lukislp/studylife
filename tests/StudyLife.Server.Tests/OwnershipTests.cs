using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Tests for the explicit AuthUserEntity.IsOwner flag (audit finding A15/A2 fix) - replaces the
/// former "AuthUser with the lowest Id" derivation. Same migration-backfill test shape as
/// MultiTenantMigrationBackfillTests (MultiTenantFoundationTests.cs).
/// </summary>
public class OwnershipMigrationBackfillTests : IDisposable
{
    // The migration immediately preceding AddAuthUserIsOwner - migrating only up to here leaves
    // AuthUsers without the IsOwner column, exactly the "pre-flag" state the backfill runs against.
    private const string PreviousMigration = "20260826111645_AddSettingsVersion";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-ownermig-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        return new StudyLifeDb(options, new TestCurrentUserAccessor());
    }

    public void Dispose()
    {
        using (var poolProbe = new SqliteConnection($"Data Source={_dbPath}"))
            SqliteConnection.ClearPool(poolProbe);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Migration_BackfillsIsOwnerOntoTheLowestIdUser_NotJustAnyUser()
    {
        using (var db = NewContext())
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            // AddMultiTenantAuthUserFoundation (part of PreviousMigration's history) already
            // seeded ONE legacy user ("Mein Studium", Id 1) - add a second, higher-Id user via
            // raw SQL (pre-flag schema has no IsOwner column yet) so the assertion below proves
            // the backfill specifically targets the LOWEST Id, not merely "the only" user.
            // Deliberately no EF LINQ query against db.AuthUsers before the migration below
            // completes - the compiled model already expects the IsOwner column, which doesn't
            // exist in the DB yet at this point (raw SQL only).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO AuthUsers (DisplayName, CreatedAt) VALUES ('Second User', '2026-01-05 10:00:00');");

            await migrator.MigrateAsync();
        }

        using (var db = NewContext())
        {
            var users = await db.AuthUsers.OrderBy(u => u.Id).ToListAsync();
            Assert.Equal(2, users.Count);
            Assert.Equal("Mein Studium", users[0].DisplayName);
            Assert.Equal("Second User", users[1].DisplayName);
            Assert.True(users[0].IsOwner);
            Assert.False(users[1].IsOwner);
        }
    }
}

/// <summary>
/// Assignment at registration time (AuthController.RegisterComplete): the first registration
/// claims the migration-seeded legacy user, which already carries IsOwner=true from the backfill
/// migration - every later registration creates a brand-new user with IsOwner staying false.
/// Own factory (fresh DB), mirrors BackupControllerOwnerRestrictionTests but asserts via
/// GET /api/auth/account-info instead of the backup 403/200 side effect.
/// </summary>
public class OwnershipRegistrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OwnershipRegistrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task FirstRegistration_ClaimsOwner_SecondRegistration_DoesNot()
    {
        using var firstKey = new FakePasskey();
        var ownerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var nonOwnerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        var ownerInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", ownerToken);
        Assert.Equal(HttpStatusCode.OK, ownerInfo.StatusCode);
        Assert.True((await ownerInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        var nonOwnerInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", nonOwnerToken);
        Assert.Equal(HttpStatusCode.OK, nonOwnerInfo.StatusCode);
        Assert.False((await nonOwnerInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        await _factory.WithDbAsync(async db =>
        {
            var users = await db.AuthUsers.OrderBy(u => u.Id).ToListAsync();
            Assert.Equal(2, users.Count);
            Assert.True(users[0].IsOwner);
            Assert.False(users[1].IsOwner);
        });
    }

}

/// <summary>
/// The genuine "zero AuthUsers exist at all" edge case (audit A15/A2 fix, RegisterComplete's
/// IsOwner = !anyPasskeyExists on the newly-created-user branch): distinct from
/// OwnershipRegistrationTests above, where the migration-seeded legacy row is always present and
/// gets CLAIMED instead of a new row being created. Simulated by wiping AuthUsers before the
/// first registration - the same technique AuthControllerDemoModeTests uses for "demo user
/// missing". Own factory/class (not just its own [Fact]): IClassFixture shares one factory
/// across every fact in a class in xUnit's unspecified order, and a sibling fact registering its
/// own users first would leave stale PasskeyCredentials rows behind that flip anyPasskeyExists
/// for this one, corrupting exactly the state this test depends on.
/// </summary>
public class OwnershipZeroUsersRegistrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OwnershipZeroUsersRegistrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterComplete_WhenNoAuthUsersExistAtAll_NewUserBecomesOwner()
    {
        await _factory.WithDbAsync(async db =>
        {
            await db.AuthSessions.ExecuteDeleteAsync();
            await db.AuthUsers.ExecuteDeleteAsync();
        });

        using var key = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Solo", key);

        var info = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", token);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.True((await info.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        await _factory.WithDbAsync(async db => Assert.True(await db.AuthUsers.SingleAsync() is { IsOwner: true }));
    }
}

/// <summary>
/// Self-healing fallback (OwnershipService.IsOwnerAsync): the only realistic way to reach a DB
/// with NO IsOwner=true row at all is restoring a raw backup taken before this feature existed
/// (BackupController) - migrations already ran and backfilled against a DIFFERENT dataset before
/// that backup file existed. Simulated directly by clearing the flag on every row after normal
/// registration, instead of exercising the actual restore pipeline (covered separately by
/// BackupRestoreTests) - this test is only about OwnershipService's own fallback/self-heal logic.
/// </summary>
public class OwnershipSelfHealTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OwnershipSelfHealTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // One scenario fact instead of several independent [Fact]s: IClassFixture shares this
    // class's factory/DB across every fact in arbitrary order (xUnit gives no guarantee), and
    // "who is the lowest-Id user" is exactly the kind of state a second, independently-ordered
    // fact registering its own users would silently corrupt (a later registration only claims
    // the legacy row on the FIRST-ever registration; a second fact's "first" registration would
    // otherwise land on a brand-new, non-lowest Id and falsify the assertions below).
    [Fact]
    public async Task NoOwnerFlagSet_FallsBackToLowestId_AndPersistsIt_AcrossBothCallSites()
    {
        using var firstKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        // Simulate a restored pre-flag backup: nobody has the flag set anymore.
        await _factory.WithDbAsync(db => db.AuthUsers.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOwner, false)));
        await _factory.WithDbAsync(async db => Assert.False(await db.AuthUsers.AnyAsync(u => u.IsOwner)));

        // The NON-lowest-Id user (Anna) must NOT self-heal into ownership just by asking first -
        // the fallback always resolves to the lowest Id, regardless of calling order.
        var annaInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", annaToken);
        Assert.False((await annaInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        // The self-heal above already persisted ownership onto Alex (the lowest Id) as a side
        // effect of Anna's check - Alex now reads true too, and a fresh DB read confirms it stuck.
        var alexInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", alexToken);
        Assert.True((await alexInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        await _factory.WithDbAsync(async db =>
        {
            var users = await db.AuthUsers.OrderBy(u => u.Id).ToListAsync();
            Assert.True(users[0].IsOwner);
            Assert.False(users[1].IsOwner);
        });

        // Clear again and verify the raw-backup call site (BackupController.IsOwnerAsync) self-
        // heals identically - it goes through the same shared OwnershipService as account-info.
        await _factory.WithDbAsync(db => db.AuthUsers.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOwner, false)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/backup/restore/status");
        request.Headers.Add("X-Session-Token", alexToken);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            var users = await db.AuthUsers.OrderBy(u => u.Id).ToListAsync();
            Assert.True(users[0].IsOwner);
            Assert.False(users[1].IsOwner);
        });
    }
}

/// <summary>
/// Demo mode (DemoSeeder wipes and re-creates the demo user on every startup, see
/// AuthControllerDemoModeTests for the base scenario): the sole demo user's Id changes on every
/// reseed, but account-info's isOwner bit must stay stable (feeds the setup UI) - see
/// DemoSeeder's explicit IsOwner=true on the seeded user.
/// </summary>
public class OwnershipDemoReseedTests : IClassFixture<AuthControllerDemoModeTests.DemoModeFactory>
{
    private readonly AuthControllerDemoModeTests.DemoModeFactory _factory;
    private readonly HttpClient _client;

    public OwnershipDemoReseedTests(AuthControllerDemoModeTests.DemoModeFactory factory)
    {
        _factory = factory;
        _client = ApiKeyTestHelpers.CreateClientWithKey(factory, null); // demo-login is unauthenticated
    }

    [Fact]
    public async Task AccountInfo_IsOwnerStaysTrue_AcrossADemoReseed()
    {
        var firstLogin = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        var firstToken = (await firstLogin.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;

        var firstInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", firstToken);
        Assert.Equal(HttpStatusCode.OK, firstInfo.StatusCode);
        Assert.True((await firstInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);

        // Container-restart scenario: a brand-new demo user with a brand-new Id (see
        // DemoSeederTests.SecondReseed_KeepsSingleUserWithFreshCalendarToken) - isOwner must
        // still read true for whoever is demo-logged-in afterward.
        await _factory.WithDbAsync(db => DemoSeeder.ReseedAsync(db));

        var secondLogin = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        var secondToken = (await secondLogin.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;

        var secondInfo = await PasskeyHttp.GetWithTokenAsync(_client, "/api/auth/account-info", secondToken);
        Assert.Equal(HttpStatusCode.OK, secondInfo.StatusCode);
        Assert.True((await secondInfo.Content.ReadFromJsonAsync<AccountInfoDto>())!.IsOwner);
    }
}
