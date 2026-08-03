using Microsoft.Data.Sqlite;

namespace StudyLife.Server.Services;

/// <summary>
/// Result of <see cref="DatabaseRestoreService.ApplyPendingRestore"/> at startup.
/// </summary>
public enum RestoreApplyStatus
{
    /// <summary>No staging file present - normal startup, nothing happened.</summary>
    NoPending,
    /// <summary>Staging file validated and adopted as the new live DB.</summary>
    Applied,
    /// <summary>Staging file failed re-validation - live DB untouched,
    /// staging file quarantined to *.rejected so no re-apply loop occurs.</summary>
    RejectedInvalid,
    /// <summary>Unexpected error (I/O, lock) - live DB remains the running state,
    /// staging file stays in place and will be retried on the next startup.</summary>
    Failed,
}

public record RestoreApplyOutcome(RestoreApplyStatus Status, string? Detail = null);

/// <summary>
/// Restores the SQLite DB from an uploaded backup (the counterpart to the download in
/// <see cref="DatabaseBackupService"/>). Deliberately NO live swap: the running app holds
/// open WAL connections to app_data/studylife.db via AddDbContextPool - overwriting the
/// file underneath an open pool would be undefined behavior for SQLite (the page cache/-wal/-shm
/// would no longer match the main file, risking corruption of OLD and new data alike). Instead:
///
///   1. The upload is written to a temp file and validated there (<see cref="Validate"/>:
///      PRAGMA integrity_check == "ok" + core tables present per sqlite_master).
///   2. Before staging, the controller takes an automatic safety backup of the
///      current live DB (<see cref="DatabaseBackupService.CreatePreRestoreBackup"/>).
///   3. The validated file gets staged to <see cref="StagingPath"/> - the live DB
///      remains completely untouched until the next restart.
///   4. On the next process start, Program.cs calls <see cref="ApplyPendingRestore"/>
///      BEFORE any DB connection is opened: re-validation, then the
///      -wal/-shm sidecars of the OLD DB get deleted (a stale -wal next to a freshly
///      swapped-in main file would otherwise let SQLite "replay" foreign writes
///      that belong to a completely different database) and the staging file gets moved
///      over the live DB via File.Move. The move removes the staging file at the same
///      time - it is itself the "pending" marker, so a normal restart afterward is a
///      no-op again. db.Database.Migrate() then runs as usual and automatically brings
///      a backup with an older schema up to the current state.
/// </summary>
public class DatabaseRestoreService
{
    /// <summary>
    /// Core tables that every real StudyLife DB has (table names = DbSet names, see
    /// Data/StudyLifeDb.cs or the CreateTable calls in Migrations/). Deliberately only the four
    /// oldest/most stable ones - a backup from an older app version may not yet have newer tables
    /// (e.g. StudyPrograms) and must still be restorable.
    /// </summary>
    public static readonly string[] RequiredTables = ["Sessions", "Settings", "Notes", "CourseGoals"];

    private readonly string _dbPath;

    /// <summary>Path of the staging file ("pending restore"), e.g. app_data/studylife.restore-pending.db.</summary>
    public string StagingPath { get; }

    public DatabaseRestoreService(string dbPath)
    {
        _dbPath = dbPath;
        StagingPath = GetStagingPath(dbPath);
    }

    /// <summary>
    /// Staging path next to the live DB, derived from the DB filename (instead of a fixed
    /// "restore-pending.db"), so parallel-running test factories with their own temp DBs in the
    /// same directory don't overwrite each other's staging file.
    /// </summary>
    public static string GetStagingPath(string dbPath) =>
        Path.Combine(Path.GetDirectoryName(dbPath)!,
            Path.GetFileNameWithoutExtension(dbPath) + ".restore-pending.db");

    public bool IsRestorePending => File.Exists(StagingPath);

    public DateTime? StagedAtUtc => IsRestorePending ? File.GetLastWriteTimeUtc(StagingPath) : null;

    /// <summary>
    /// Checks whether <paramref name="candidatePath"/> is an intact StudyLife SQLite DB.
    /// Returns null on success, otherwise an (English) error description for the 400 response.
    /// Deliberately opens ReadOnly + Pooling=False: no write sidecars, no file lock held
    /// after Dispose (under Windows, a pooled connection would otherwise lock the file).
    /// </summary>
    public static string? Validate(string candidatePath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={candidatePath};Mode=ReadOnly;Pooling=False");
            connection.Open();

            using (var integrity = connection.CreateCommand())
            {
                // integrity_check returns multiple rows on failure - ExecuteScalar reads the
                // first one; anything other than exactly "ok" is a failure.
                integrity.CommandText = "PRAGMA integrity_check;";
                if (integrity.ExecuteScalar() as string != "ok")
                    return "The file failed SQLite's integrity check (corrupt or truncated database).";
            }

            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = tables.ExecuteReader())
            {
                while (reader.Read()) names.Add(reader.GetString(0));
            }

            var missing = RequiredTables.Where(t => !names.Contains(t)).ToList();
            if (missing.Count > 0)
                return $"The file is a SQLite database but not a StudyLife backup (missing tables: {string.Join(", ", missing)}).";

            return null;
        }
        catch (SqliteException ex)
        {
            return $"The file is not a valid SQLite database (SQLite error {ex.SqliteErrorCode}).";
        }
    }

    /// <summary>
    /// Copies the already-validated file to <see cref="StagingPath"/>. First writes to
    /// a .tmp file in the same directory and then renames it - a crash mid-copy thus
    /// never leaves behind a half-written "valid-looking" staging file (and the re-validation
    /// in <see cref="ApplyPendingRestore"/> would catch even that anyway).
    /// The live DB is not touched in any way here.
    /// </summary>
    public void Stage(string validatedSourcePath)
    {
        DeleteSidecars(StagingPath);
        var tempTarget = StagingPath + ".tmp";
        File.Copy(validatedSourcePath, tempTarget, overwrite: true);
        File.Move(tempTarget, StagingPath, overwrite: true);
    }

    /// <summary>Discards a staged restore. True if one was present.</summary>
    public bool CancelPending()
    {
        if (!IsRestorePending) return false;
        DeleteSidecars(StagingPath);
        File.Delete(StagingPath);
        return true;
    }

    /// <summary>
    /// Startup hook: applies a staged restore if one is present. MUST run before
    /// any connection to <paramref name="dbPath"/> is opened (Program.cs calls this
    /// right after the path calculation, before AddDbContextPool/Migrate). Extracted as a
    /// static, independently testable method instead of living inline in the top-level
    /// statements - BackupRestoreTests uses this to simulate the "next restart".
    /// Never throws: every failure leaves the existing live DB running untouched.
    /// </summary>
    public static RestoreApplyOutcome ApplyPendingRestore(string dbPath)
    {
        var stagingPath = GetStagingPath(dbPath);
        if (!File.Exists(stagingPath))
            return new RestoreApplyOutcome(RestoreApplyStatus.NoPending);

        try
        {
            // Re-validation - cheap insurance against anything that could have corrupted the
            // staging file between staging and restart (full disk, etc.).
            var error = Validate(stagingPath);
            if (error != null)
            {
                // Quarantine instead of deletion (diagnosis remains possible), but out of the
                // way in any case - otherwise every further startup would hit the same failure again.
                var rejectedPath = stagingPath + ".rejected";
                if (File.Exists(rejectedPath)) File.Delete(rejectedPath);
                DeleteSidecars(stagingPath);
                File.Move(stagingPath, rejectedPath);
                return new RestoreApplyOutcome(RestoreApplyStatus.RejectedInvalid, error);
            }

            // At a real process start, no connections exist yet; in tests (host is still
            // running) this releases the file handles pooled by Microsoft.Data.Sqlite, which
            // would otherwise lock the live DB under Windows.
            SqliteConnection.ClearAllPools();

            // Sidecars of the OLD DB go first: a leftover -wal file next to the
            // freshly swapped-in main file could make SQLite "replay" writes on first
            // open that belong to a completely different database.
            // (After a clean shutdown, neither exists anyway; after a
            // crash, the -wal only contains writes of the old DB, which the user is
            // deliberately replacing entirely right now - loss here is intentional, not accidental.)
            DeleteSidecars(dbPath);
            // Sidecars of the staging file can only come from our own read-only validation
            // (the upload is a single main file) - empty in content, gone with it.
            DeleteSidecars(stagingPath);

            // The move replaces the live DB and simultaneously removes the staging file (= marker):
            // the next normal restart finds nothing left and is a no-op again.
            File.Move(stagingPath, dbPath, overwrite: true);
            return new RestoreApplyOutcome(RestoreApplyStatus.Applied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Don't throw: a crash loop at startup would be worse than a restore that only
            // succeeds on the next restart. The live DB is still usable on every error path
            // (worst case without the -wal tail, see above - consistent, just without the last
            // writes that were going to be replaced anyway).
            return new RestoreApplyOutcome(RestoreApplyStatus.Failed, ex.Message);
        }
    }

    private static void DeleteSidecars(string mainDbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = mainDbPath + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }
}
