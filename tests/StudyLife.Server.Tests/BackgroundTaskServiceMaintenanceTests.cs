using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>RunDatabaseMaintenanceAsync: pure smoke test against a real (temporary) SQLite file.</summary>
public class BackgroundTaskServiceDatabaseMaintenanceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceDatabaseMaintenanceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient(); // ensures the host (and therefore the DB migration) has started
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RunsVacuumAndWalCheckpoint_WithoutThrowing_AndDbStaysUsableAfterward()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            seedDb.Notes.Add(new NoteEntity { Title = "Before Maintenance", Content = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await seedDb.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            await _service.RunDatabaseMaintenanceAsync(db);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            // DB must still be normally readable/writable after VACUUM + wal_checkpoint.
            Assert.True(await db.Notes.AsNoTracking().AnyAsync(n => n.Title == "Before Maintenance"));
        }
    }
}

/// <summary>
/// The Postgres skip of RunDatabaseMaintenanceAsync: VACUUM/wal_checkpoint are SQLite concepts,
/// in Postgres mode the method must return WITHOUT touching the database (autovacuum takes
/// over). IsNpgsql() only inspects the configured provider - a context with Npgsql options
/// against a never-reachable host proves the skip happens before any connection attempt.
/// </summary>
public class BackgroundTaskServicePostgresMaintenanceSkipTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePostgresMaintenanceSkipTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task PostgresProvider_SkipsSqliteMaintenance_WithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDbPostgres>()
            .UseNpgsql("Host=localhost;Port=1;Database=studylife_never_connects;Username=x;Password=x")
            .Options;
        using var db = new StudyLifeDbPostgres(options,
            new CurrentUserAccessor(new Microsoft.AspNetCore.Http.HttpContextAccessor()));

        // Must complete instantly and silently - a VACUUM attempt against the bogus host would
        // throw a connection error here.
        await _service.RunDatabaseMaintenanceAsync(db);
    }
}

/// <summary>
/// The Postgres skip of RunBackupDumpAsync: without a registered DatabaseBackupService
/// (Postgres mode, see Program.cs) the dump must be a completed no-op - no directory, no file.
/// Own factory, so the sibling dump test's real backup file can't leak into the assertion.
/// </summary>
public class BackgroundTaskServiceBackupDumpWithoutServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public BackgroundTaskServiceBackupDumpWithoutServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
    }

    [Fact]
    public async Task NoBackupService_IsCompletedNoOp_CreatesNothing()
    {
        // Direct construction WITHOUT backupService - exactly the Postgres-mode registration.
        var service = new BackgroundTaskService(
            _factory.Services,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            _factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
            _factory.Services.GetRequiredService<ApnsSender>());

        await service.RunBackupDumpAsync();

        Assert.False(Directory.Exists(Path.Combine(_factory.BackupContentRoot, "app_data", "backups")));
    }
}

/// <summary>RunBackupDumpAsync: checks the orchestration (call to DatabaseBackupService.CreateWeeklyBackup), not its internals (see DatabaseBackupServiceTests for that).</summary>
public class BackgroundTaskServiceBackupDumpTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceBackupDumpTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task CreatesDatedBackupFileUnderBackupContentRoot()
    {
        await _service.RunBackupDumpAsync();

        var backupDir = Path.Combine(_factory.BackupContentRoot, "app_data", "backups");
        var expectedFile = Path.Combine(backupDir, $"studylife-{DateTime.UtcNow:yyyyMMdd}.db");
        Assert.True(File.Exists(expectedFile), $"Erwartete Backup-Datei fehlt: {expectedFile}");
    }
}
