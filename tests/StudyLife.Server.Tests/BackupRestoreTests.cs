using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Integration tests for POST /api/backup/restore and the startup apply step
/// (DatabaseRestoreService.ApplyPendingRestore - extracted from Program.cs' top-level
/// statements into a standalone static method for exactly this purpose). Deliberately NO
/// IClassFixture: every test gets its own factory/temp DB, because staging/apply manipulate
/// DB files and tests would otherwise wreck each other's state.
/// CustomWebApplicationFactory already redirects DatabaseRestoreService to the temp DB -
/// staging therefore never happens next to the real app_data DB.
/// </summary>
public class BackupRestoreTests
{
    // ---------- Helpers ----------

    // The marker lives in Topic, not CourseName (audit finding M2: POST /api/sessions now
    // derives CourseName server-side from the resolved catalog course, so a client-supplied
    // value there can no longer distinguish sessions for this raw-backup round-trip test).
    private static StudySessionDto MakeSession(string topicMarker) => new()
    {
        CourseId = 1,
        CourseName = "irrelevant",
        StartTime = DateTime.Today.AddDays(-1).AddHours(10),
        EndTime = DateTime.Today.AddDays(-1).AddHours(11),
        Topic = topicMarker,
        IsCompleted = false,
        TimerModeId = 1,
    };

    private static MultipartFormDataContent MakeUpload(byte[] bytes, string fileName = "studylife-backup.db")
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { fileContent, "file", fileName } };
    }

    /// <summary>
    /// Reads a file that SQLite keeps open concurrently with FileShare.ReadWrite - File.ReadAllBytes
    /// (FileShare.Read) would fail on Windows against the existing write share.
    /// </summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static long CountSessionsByName(string dbPath, string topicMarker)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sessions WHERE Topic = $topic;";
        command.Parameters.AddWithValue("$topic", topicMarker);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static string BackupDir(CustomWebApplicationFactory factory)
        => Path.Combine(factory.BackupContentRoot, "app_data", "backups");

    /// <summary>Small, real SQLite file with a foreign schema (not a StudyLife backup).</summary>
    private static byte[] CreateForeignSqliteDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studylife-foreign-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE SomethingElse (Id INTEGER PRIMARY KEY, Value TEXT);";
                command.ExecuteNonQuery();
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // ---------- Round-Trip ----------

    [Fact]
    public async Task RoundTrip_StageLeavesLiveDbUntouched_ApplyRestoresBackupState()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var courseA = $"RestoreCourseA-{Guid.NewGuid():N}";
        var courseB = $"RestoreCourseB-{Guid.NewGuid():N}";

        // 1. Create data that should end up in the backup.
        var createA = await client.PostAsJsonAsync("/api/sessions", MakeSession(courseA));
        Assert.Equal(HttpStatusCode.OK, createA.StatusCode);

        // 2. Pull a backup via the existing download endpoint.
        var download = await client.GetAsync("/api/backup/database");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var backupBytes = await download.Content.ReadAsByteArrayAsync();

        // 3. Create FURTHER data - the live state now differs from the backup.
        var createB = await client.PostAsJsonAsync("/api/sessions", MakeSession(courseB));
        Assert.Equal(HttpStatusCode.OK, createB.StatusCode);

        var stagingPath = DatabaseRestoreService.GetStagingPath(factory.DbPath);
        Assert.False(File.Exists(stagingPath));

        // Baseline for the "live DB untouched" proof. First do a full checkpoint: in WAL mode
        // a checkpoint may legitimately fold committed -wal contents into the main file at any
        // time (that's exactly what e.g. closing a backup source connection triggers) - that's
        // background hygiene, not "touching" the data. With an empty WAL, the main file is
        // afterward only byte-different if someone actually writes to it.
        using (var checkpoint = new SqliteConnection($"Data Source={factory.DbPath};Pooling=False"))
        {
            checkpoint.Open();
            using var cmd = checkpoint.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        var liveBytesBeforeStage = ReadAllBytesShared(factory.DbPath);

        // 4. Upload the backup -> 202 staged, NO immediate apply.
        using (var upload = MakeUpload(backupBytes))
        {
            var restore = await client.PostAsync("/api/backup/restore", upload);
            Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);

            using var body = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
            Assert.Equal("staged", body.RootElement.GetProperty("status").GetString());
            Assert.StartsWith("prerestore-", body.RootElement.GetProperty("safetyBackup").GetString());
        }

        // 5. Staging file exists and matches the upload.
        Assert.True(File.Exists(stagingPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(stagingPath));

        // 6. Safety backup was taken BEFORE staging and contains the
        //    pre-restore state (including session B).
        var safetyBackups = Directory.GetFiles(BackupDir(factory), "prerestore-*.db");
        var safetyBackup = Assert.Single(safetyBackups);
        Assert.Equal(1, CountSessionsByName(safetyBackup, courseA));
        Assert.Equal(1, CountSessionsByName(safetyBackup, courseB));

        // 7. Live DB is untouched after staging: main file byte-identical to the baseline
        //    (WAL emptied beforehand, staging itself writes nothing to the live DB),
        //    logical content still A AND B (B is missing from the backup - had the file
        //    already been replaced, B would be gone), and the API sees the same thing.
        Assert.Equal(liveBytesBeforeStage, ReadAllBytesShared(factory.DbPath));
        Assert.Equal(1, CountSessionsByName(factory.DbPath, courseA));
        Assert.Equal(1, CountSessionsByName(factory.DbPath, courseB));
        var sessions = await client.GetFromJsonAsync<List<StudySessionDto>>("/api/sessions");
        Assert.NotNull(sessions);
        Assert.Contains(sessions!, s => s.Topic == courseA);
        Assert.Contains(sessions!, s => s.Topic == courseB);

        // 8. Status endpoint reports the pending restore (basis for the UI banner).
        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/backup/restore/status")))
        {
            Assert.True(status.RootElement.GetProperty("pending").GetBoolean());
        }

        // 9. "Next restart": exactly the method that Program.cs calls before
        //    AddDbContextPool/Migrate(). ClearAllPools stands in here for process exit
        //    (releases the file handles pooled by Microsoft.Data.Sqlite, as a restart would).
        SqliteConnection.ClearAllPools();
        var outcome = DatabaseRestoreService.ApplyPendingRestore(factory.DbPath);
        Assert.Equal(RestoreApplyStatus.Applied, outcome.Status);

        // 10. Marker gone (no re-apply on the next start), no leftover sidecar files,
        //     second call is a no-op.
        Assert.False(File.Exists(stagingPath));
        Assert.False(File.Exists(stagingPath + "-wal"));
        Assert.False(File.Exists(stagingPath + "-shm"));
        Assert.Equal(RestoreApplyStatus.NoPending, DatabaseRestoreService.ApplyPendingRestore(factory.DbPath).Status);

        // 11. The live DB is now the BACKUP state: session A yes, session B no.
        Assert.Equal(1, CountSessionsByName(factory.DbPath, courseA));
        Assert.Equal(0, CountSessionsByName(factory.DbPath, courseB));
    }

    // ---------- Validation / 400s ----------

    [Fact]
    public async Task Restore_RejectsNonSqliteFile()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        using var upload = MakeUpload(Encoding.UTF8.GetBytes("this is definitely not a sqlite database, just text"));
        var response = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync());
        // Nothing staged, and because validation fails BEFORE the safety backup,
        // none was taken either.
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
        Assert.False(Directory.Exists(BackupDir(factory))
                     && Directory.GetFiles(BackupDir(factory), "prerestore-*.db").Length > 0);
    }

    [Fact]
    public async Task Restore_RejectsCorruptTruncatedSqliteFile()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Get a real backup and brutally truncate it - the SQLite header is still fine,
        // integrity_check/opening must still fail.
        var download = await client.GetAsync("/api/backup/database");
        var backupBytes = await download.Content.ReadAsByteArrayAsync();
        var truncated = backupBytes.Take(backupBytes.Length / 3).ToArray();
        Assert.True(truncated.Length >= 16); // otherwise this test only exercises the empty-file path

        using var upload = MakeUpload(truncated);
        var response = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
    }

    [Fact]
    public async Task Restore_RejectsValidSqliteWithWrongSchema()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        using var upload = MakeUpload(CreateForeignSqliteDb());
        var response = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("missing tables", body);
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
    }

    [Fact]
    public async Task Restore_RejectsEmptyUpload()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        using var upload = MakeUpload(Array.Empty<byte>());
        var response = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Status / Cancel / restart guard ----------

    [Fact]
    public async Task RestoreStatus_NotPendingByDefault_CancelRemovesStagedFile()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/backup/restore/status")))
        {
            Assert.False(status.RootElement.GetProperty("pending").GetBoolean());
        }

        // Cancel without staging -> 404.
        var cancelNothing = await client.PostAsync("/api/backup/restore/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, cancelNothing.StatusCode);

        // Stage, then discard.
        var backupBytes = await (await client.GetAsync("/api/backup/database")).Content.ReadAsByteArrayAsync();
        using (var upload = MakeUpload(backupBytes))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/backup/restore", upload)).StatusCode);
        }
        var stagingPath = DatabaseRestoreService.GetStagingPath(factory.DbPath);
        Assert.True(File.Exists(stagingPath));

        var cancel = await client.PostAsync("/api/backup/restore/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.False(File.Exists(stagingPath));

        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/backup/restore/status")))
        {
            Assert.False(status.RootElement.GetProperty("pending").GetBoolean());
        }
    }

    [Fact]
    public async Task RestartEndpoint_RefusesWithoutStagedRestore()
    {
        // 409 guard: no generic remote kill. We deliberately do NOT test the success case
        // (StopApplication) over HTTP - it would shut down the test host.
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/backup/restore/restart", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------- Startup apply directly (without host) ----------

    [Fact]
    public void ApplyPendingRestore_NoStagingFile_IsNoOp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"studylife-apply-{Guid.NewGuid():N}.db");
        try
        {
            Assert.Equal(RestoreApplyStatus.NoPending, DatabaseRestoreService.ApplyPendingRestore(dbPath).Status);
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public void ApplyPendingRestore_CorruptStagedFile_QuarantinesAndKeepsLiveDb()
    {
        // Simulates corruption BETWEEN staging and restart: the re-validation in the
        // apply step must catch it, the live DB must stay untouched, and the broken
        // staging file must be quarantined (no failure loop on every start).
        var dbPath = Path.Combine(Path.GetTempPath(), $"studylife-apply-{Guid.NewGuid():N}.db");
        var stagingPath = DatabaseRestoreService.GetStagingPath(dbPath);
        var rejectedPath = stagingPath + ".rejected";
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Live (Id INTEGER PRIMARY KEY); INSERT INTO Live (Id) VALUES (42);";
                command.ExecuteNonQuery();
            }
            var liveBytes = File.ReadAllBytes(dbPath);

            File.WriteAllBytes(stagingPath, Encoding.UTF8.GetBytes("garbage that is not sqlite"));

            var outcome = DatabaseRestoreService.ApplyPendingRestore(dbPath);

            Assert.Equal(RestoreApplyStatus.RejectedInvalid, outcome.Status);
            Assert.NotNull(outcome.Detail);
            Assert.Equal(liveBytes, File.ReadAllBytes(dbPath)); // live DB byte-identical
            Assert.False(File.Exists(stagingPath));             // no re-apply attempt
            Assert.True(File.Exists(rejectedPath));             // diagnostic file preserved
        }
        finally
        {
            foreach (var file in new[] { dbPath, stagingPath, rejectedPath })
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
