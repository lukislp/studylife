using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Edge paths of DatabaseRestoreService that the HTTP-level BackupRestore suite doesn't reach:
/// a database that opens fine but fails PRAGMA integrity_check (bit rot inside the file, not
/// a wrong format), and the Failed outcome of ApplyPendingRestore when the live DB can't be
/// replaced (I/O error) - which must leave both the live DB and the staging file in place so
/// the restore is retried on the next startup instead of being lost.
/// Plain unit tests against temp files, no host required (same pattern as DatabaseBackupServiceTests).
/// </summary>
public class DatabaseRestoreServiceEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"studylife-restoresvc-{Guid.NewGuid():N}");

    public DatabaseRestoreServiceEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    /// <summary>Creates a real StudyLife schema (EnsureCreated builds all tables incl. the
    /// four RequiredTables) at <paramref name="path"/> and seeds one row so the file spans
    /// enough pages to corrupt one in the middle.</summary>
    private static void CreateValidStudyLifeDb(string path)
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={path};Pooling=False").Options;
        using var db = new StudyLifeDb(options, new TestCurrentUserAccessor());
        db.Database.EnsureCreated();
        db.Notes.Add(new NoteEntity
        {
            Title = "seed",
            Content = new string('x', 10_000),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public void Validate_BitRotInsideTheFile_FailsTheIntegrityCheck()
    {
        // A file whose header/schema still parse (so it "is" a SQLite DB) but whose inner
        // pages are trashed: the open succeeds, PRAGMA integrity_check must catch it.
        var path = PathIn("corrupt.db");
        CreateValidStudyLifeDb(path);
        SqliteConnection.ClearAllPools();

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.True(fs.Length > 3 * 4096); // sanity: enough pages to corrupt a middle one
            // Trash two full pages in the middle of the file, leaving page 1 (header +
            // sqlite_master root) intact so the connection still opens cleanly.
            fs.Seek(2 * 4096, SeekOrigin.Begin);
            var garbage = new byte[2 * 4096];
            Array.Fill(garbage, (byte)0xFF);
            fs.Write(garbage);
        }

        var error = DatabaseRestoreService.Validate(path);

        Assert.NotNull(error);
        Assert.Contains("integrity check", error);
    }

    [Fact]
    public void ApplyPendingRestore_LiveDbLocked_ReturnsFailedAndKeepsStagingForRetry()
    {
        // The documented "never throw at startup" contract: if the swap itself fails (here: the
        // live DB is exclusively locked, as an AV scanner or a lingering handle could), the
        // outcome is Failed with a detail, the live DB keeps its old content, and the staging
        // file survives untouched so the NEXT restart retries the restore.
        var livePath = PathIn("live.db");
        CreateValidStudyLifeDb(livePath);
        var stagingPath = DatabaseRestoreService.GetStagingPath(livePath);
        CreateValidStudyLifeDb(stagingPath);
        SqliteConnection.ClearAllPools();

        RestoreApplyOutcome outcome;
        using (File.Open(livePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            outcome = DatabaseRestoreService.ApplyPendingRestore(livePath);
        }

        Assert.Equal(RestoreApplyStatus.Failed, outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Detail));
        Assert.True(File.Exists(stagingPath)); // still pending - retried on next startup
        Assert.True(File.Exists(livePath));

        // After the lock is gone, the very same retry must succeed.
        var retry = DatabaseRestoreService.ApplyPendingRestore(livePath);
        Assert.Equal(RestoreApplyStatus.Applied, retry.Status);
        Assert.False(File.Exists(stagingPath));
    }
}
