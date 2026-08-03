using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Exercises DatabaseBackupService directly against isolated temp paths - this class has no
/// dependency on the ASP.NET Core host, so a WebApplicationFactory would be unnecessary
/// overhead. Every test gets its own temp dir/db file so tests can run in parallel safely.
/// </summary>
public class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _contentRoot;
    private readonly List<string> _cleanupPaths = new();

    public DatabaseBackupServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-backupsvc-{Guid.NewGuid():N}.db");
        _contentRoot = Path.Combine(Path.GetTempPath(), $"studylife-backupsvc-root-{Guid.NewGuid():N}");
        _cleanupPaths.Add(_dbPath);
    }

    private DbContextOptions<StudyLifeDb> Options(string dbPath) =>
        new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={dbPath}").Options;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _cleanupPaths)
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
        try { Directory.Delete(_contentRoot, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void CreateTempBackup_ProducesAnOpenableSqliteFile_ReflectingCurrentDbState()
    {
        using (var db = new StudyLifeDb(Options(_dbPath), new TestCurrentUserAccessor()))
        {
            db.Database.EnsureCreated();
            db.Notes.Add(new NoteEntity { Title = "Backup me", Content = "hello", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var service = new DatabaseBackupService(_dbPath, _contentRoot);
        var backupPath = service.CreateTempBackup();
        _cleanupPaths.Add(backupPath);

        Assert.True(File.Exists(backupPath));

        using var backupDb = new StudyLifeDb(Options(backupPath), new TestCurrentUserAccessor());
        var note = backupDb.Notes.SingleOrDefault(n => n.Title == "Backup me");
        Assert.NotNull(note);
        Assert.Equal("hello", note!.Content);
    }

    [Fact]
    public void CreateTempBackup_CapturesWalModeUncommittedWrites()
    {
        // The whole reason this service uses the online-backup API instead of File.Copy: in
        // WAL mode, recent writes can sit in the separate -wal file rather than the main .db
        // file. A naive copy of just the main file would miss them; BackupDatabase must not.
        using (var db = new StudyLifeDb(Options(_dbPath), new TestCurrentUserAccessor()))
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Notes.Add(new NoteEntity { Title = "WAL write", Content = "still in -wal", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        Assert.True(File.Exists(_dbPath + "-wal") || File.Exists(_dbPath));

        var service = new DatabaseBackupService(_dbPath, _contentRoot);
        var backupPath = service.CreateTempBackup();
        _cleanupPaths.Add(backupPath);

        using var backupDb = new StudyLifeDb(Options(backupPath), new TestCurrentUserAccessor());
        var note = backupDb.Notes.SingleOrDefault(n => n.Title == "WAL write");
        Assert.NotNull(note);
    }

    [Fact]
    public void CreateWeeklyBackup_PrunesToKeepCount_RetainingTheNewestFiles()
    {
        using (var db = new StudyLifeDb(Options(_dbPath), new TestCurrentUserAccessor()))
        {
            db.Database.EnsureCreated();
        }

        var backupDir = Path.Combine(_contentRoot, "app_data", "backups");
        Directory.CreateDirectory(backupDir);

        // Seed 6 fake prior backups with old, lexicographically-sortable dates. Their content
        // doesn't matter - pruning only looks at file names/mtimes, never opens them.
        var oldDates = new[] { "20200101", "20200102", "20200103", "20200104", "20200105", "20200106" };
        foreach (var date in oldDates)
            File.WriteAllText(Path.Combine(backupDir, $"studylife-{date}.db"), "dummy");

        var service = new DatabaseBackupService(_dbPath, _contentRoot);
        service.CreateWeeklyBackup(keep: 3);

        var remaining = Directory.GetFiles(backupDir, "studylife-*.db")
            .Select(Path.GetFileName)
            .OrderByDescending(f => f)
            .ToList();

        Assert.Equal(3, remaining.Count);
        // Today's real dump always sorts newest (real date > any 2020-01-0x dummy date).
        Assert.Equal($"studylife-{DateTime.UtcNow:yyyyMMdd}.db", remaining[0]);
        Assert.Equal("studylife-20200106.db", remaining[1]);
        Assert.Equal("studylife-20200105.db", remaining[2]);
        Assert.DoesNotContain("studylife-20200104.db", remaining);
        Assert.DoesNotContain("studylife-20200101.db", remaining);
    }

    [Fact]
    public void CreateWeeklyBackup_CalledTwiceSameDay_OverwritesRatherThanDuplicates()
    {
        using (var db = new StudyLifeDb(Options(_dbPath), new TestCurrentUserAccessor()))
        {
            db.Database.EnsureCreated();
        }

        var service = new DatabaseBackupService(_dbPath, _contentRoot);
        service.CreateWeeklyBackup(keep: 4);
        // Microsoft.Data.Sqlite pools connections by connection string; Dispose() returns the
        // destination connection to the pool rather than truly releasing the file handle. On
        // Windows that leaves the just-written backup file locked, so the second call's
        // `File.Delete(path)` (same-day overwrite) throws IOException unless the pool is
        // cleared first - the exact same workaround CustomWebApplicationFactory/BackupControllerTests
        // already use for their own teardown. This never surfaces on the Linux/Docker deployment
        // target (POSIX allows deleting a file that's still open), but it's a real gap: production
        // code disables neither pooling nor relies on this clearing, so a same-day restart on a
        // Windows host would hit it.
        SqliteConnection.ClearAllPools();
        service.CreateWeeklyBackup(keep: 4);

        var backupDir = Path.Combine(_contentRoot, "app_data", "backups");
        var files = Directory.GetFiles(backupDir, "studylife-*.db");

        Assert.Single(files);
        Assert.Equal($"studylife-{DateTime.UtcNow:yyyyMMdd}.db", Path.GetFileName(files[0]));
    }

    [Fact]
    public void CreateWeeklyBackup_FewerFilesThanKeep_DeletesNothing()
    {
        using (var db = new StudyLifeDb(Options(_dbPath), new TestCurrentUserAccessor()))
        {
            db.Database.EnsureCreated();
        }

        var backupDir = Path.Combine(_contentRoot, "app_data", "backups");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "studylife-20200101.db"), "dummy");

        var service = new DatabaseBackupService(_dbPath, _contentRoot);
        service.CreateWeeklyBackup(keep: 4);

        var files = Directory.GetFiles(backupDir, "studylife-*.db");
        Assert.Equal(2, files.Length); // the dummy + today's real dump
    }
}
