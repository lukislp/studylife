using Microsoft.Data.Sqlite;

namespace StudyLife.Server.Services;

/// <summary>
/// Creates consistent copies of the SQLite database via SQLite's online backup API
/// (sqlite3_backup_init/step/finish, wrapped in <see cref="SqliteConnection.BackupDatabase"/>).
/// A naive <c>File.Copy</c> of the .db file would be unsafe in WAL mode: not yet
/// checkpointed writes sit in the separate -wal file and would be missing from a
/// plain file copy (verified in a scratch test: a naive copy of the main file
/// without the corresponding -wal file didn't even contain the previously created schema).
/// Singleton, injected into both <see cref="BackupController"/> and
/// <see cref="BackgroundTaskService"/>, so the backup logic exists in exactly one place.
/// </summary>
public class DatabaseBackupService
{
    private readonly string _dbPath;
    private readonly string _backupDir;

    public DatabaseBackupService(string dbPath, string contentRootPath)
    {
        _dbPath = dbPath;
        _backupDir = Path.Combine(contentRootPath, "app_data", "backups");
    }

    /// <summary>
    /// Creates a consistent copy of the live DB at <paramref name="destinationPath"/> via
    /// the online backup API. Both connections are freshly opened for the call and
    /// closed again afterward - no pooling needed, this runs at most once a week
    /// or on a manual download.
    /// </summary>
    private void CreateBackup(string destinationPath)
    {
        using var source = new SqliteConnection($"Data Source={_dbPath}");
        source.Open();
        // Pooling=False: Microsoft.Data.Sqlite otherwise pools connections per connection string and
        // keeps the native handle/file lock open beyond Dispose() (visible on Windows as
        // a locked file). Destination paths here are always fresh (temp GUID or daily
        // dump) and get read/overwritten/deleted by the callers (BackupController, CreateWeeklyBackup)
        // right after the backup - so Dispose() must release the file immediately.
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>
    /// Writes a backup to a temporary file outside app_data and returns its
    /// path - for the download endpoint, which streams the bytes afterward and deletes the
    /// file again in the finally block.
    /// </summary>
    public string CreateTempBackup()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"studylife-backup-{Guid.NewGuid():N}.db");
        CreateBackup(tempPath);
        return tempPath;
    }

    /// <summary>
    /// Automatic safety backup taken immediately BEFORE staging a restore
    /// (BackupController.Restore) - the safety net for a botched restore, taken
    /// unprompted. Uses the same CreateBackup path (online backup API) as download
    /// and the weekly dump, deliberately no second backup code path. Prefix "prerestore-" instead
    /// of "studylife-", so CreateWeeklyBackup's cleanup glob (studylife-*.db) neither counts
    /// nor deletes these files; its own retention analogously (the newest <paramref name="keep"/>
    /// are kept). Returns the full path of the written backup.
    /// </summary>
    public string CreatePreRestoreBackup(int keep = 3)
    {
        Directory.CreateDirectory(_backupDir);

        var path = Path.Combine(_backupDir, $"prerestore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        if (File.Exists(path)) File.Delete(path);
        CreateBackup(path);

        var stale = Directory.GetFiles(_backupDir, "prerestore-*.db")
            .OrderByDescending(f => f) // timestamp in the filename sorts lexicographically = chronologically
            .Skip(keep);
        foreach (var f in stale) File.Delete(f);

        return path;
    }

    /// <summary>
    /// Weekly background dump: writes to app_data/backups/studylife-{yyyyMMdd}.db
    /// and keeps only the <paramref name="keep"/> newest files (the rest gets deleted),
    /// so the Docker volume doesn't grow unbounded. If a dump for the same day already
    /// exists (e.g. after a restart on the same day), it gets overwritten instead of duplicated.
    /// </summary>
    public void CreateWeeklyBackup(int keep = 4)
    {
        Directory.CreateDirectory(_backupDir);

        var path = Path.Combine(_backupDir, $"studylife-{DateTime.UtcNow:yyyyMMdd}.db");
        if (File.Exists(path)) File.Delete(path);
        CreateBackup(path);

        var stale = Directory.GetFiles(_backupDir, "studylife-*.db")
            .OrderByDescending(f => f) // yyyyMMdd in the filename sorts lexicographically = chronologically
            .Skip(keep);
        foreach (var f in stale) File.Delete(f);
    }
}
