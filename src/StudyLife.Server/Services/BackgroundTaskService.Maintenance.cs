using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // internal instead of private: allows StudyLife.Server.Tests (InternalsVisibleTo in the csproj)
    // to call the method directly instead of only reaching it indirectly via the 30s
    // ExecuteAsync loop - see BackgroundTaskServiceTests.
    internal async Task RunDatabaseMaintenanceAsync(StudyLifeDb db)
    {
        // VACUUM/PRAGMA wal_checkpoint are pure SQLite concepts - in Postgres mode,
        // autovacuum takes over the same role automatically, no equivalent needed here (a deliberate
        // scope cut of the scalability branch, see docs/SCALING.md).
        if (db.Database.IsNpgsql())
        {
            _logger.LogDebug("Postgres mode: SQLite maintenance (VACUUM/wal_checkpoint) skipped (autovacuum takes over).");
            return;
        }

        // Deliberately VACUUM instead of "PRAGMA incremental_vacuum": the latter only applies with
        // auto_vacuum=INCREMENTAL, but the DB runs with the SQLite default auto_vacuum=NONE
        // (never changed), where it would be a no-op. VACUUM must not run inside a transaction -
        // ExecuteSqlRawAsync doesn't start one, so this is safe here; the brief
        // exclusive lock is uncritical with a weekly run and busy_timeout=5000.
        await db.Database.ExecuteSqlRawAsync("VACUUM;");

        // Checkpoint deliberately AFTER the VACUUM: in WAL mode, VACUUM also commits via the
        // -wal file; the main DB only shrinks with the next checkpoint (verified in a scratch
        // test). TRUNCATE replays the WAL content back and truncates the -wal file to
        // 0 bytes, which would otherwise grow unboundedly without an occasional full checkpoint.
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");

        _logger.LogDebug("SQLite maintenance completed (VACUUM + wal_checkpoint(TRUNCATE))");
    }

    /// <summary>
    /// Weekly safety dump in addition to the manual download (BackupController):
    /// writes a copy to app_data/backups/ via the same online backup API (DatabaseBackupService,
    /// WAL-safe) and keeps only the last 4 weeks. No StudyLifeDb
    /// parameter needed - the service opens its own SQLite connections directly.
    /// </summary>
    internal Task RunBackupDumpAsync()
    {
        // Raw file backup is a SQLite-only feature (online backup API onto a single local
        // file) - not registered in Postgres mode (Program.cs), a deliberate scope cut
        // of the scalability branch (see docs/SCALING.md). JSON export remains available
        // across providers independently of this (BackupController.Export).
        if (_backupService is null)
        {
            _logger.LogDebug("Postgres mode: raw database backup not available, skipped.");
            return Task.CompletedTask;
        }

        // File I/O instead of an EF query - deliberately on a thread pool thread instead of
        // blocking the tick loop itself, analogous to the brief exclusive access of VACUUM above.
        return Task.Run(() =>
        {
            _backupService.CreateWeeklyBackup();
            _logger.LogInformation("Weekly database backup created");
        });
    }
}
