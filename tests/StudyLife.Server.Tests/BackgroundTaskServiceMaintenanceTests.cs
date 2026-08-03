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
