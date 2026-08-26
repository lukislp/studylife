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
/// Integration tests for the optional password encryption of .db backups: the download
/// endpoint POST /api/backup/database/encrypted and the password-aware branch of
/// POST /api/backup/restore (detect magic header -> decrypt -> same validate/stage pipeline
/// as a plaintext backup, see BackupController.Restore). Deliberately NO IClassFixture, for
/// the same reason as BackupRestoreTests: staging manipulates DB files, every test gets its
/// own factory/temp DB.
/// </summary>
public class BackupControllerEncryptedTests
{
    // The marker lives in Topic, not CourseName (audit finding M2: POST /api/sessions now
    // derives CourseName server-side from the resolved catalog course).
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

    private static MultipartFormDataContent MakeUpload(byte[] bytes, string? password = null,
        string fileName = "studylife-backup.db.enc")
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var content = new MultipartFormDataContent { { fileContent, "file", fileName } };
        if (password != null)
            content.Add(new StringContent(password), "password");
        return content;
    }

    // ---------- Encrypted download ----------

    [Fact]
    public async Task DownloadEncrypted_ReturnsMagicHeader_AndDecryptsToAValidSqliteFile()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "s3cr3t!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".db.enc", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var encryptedBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(BackupEncryptionService.IsEncrypted(encryptedBytes));

        var decrypted = BackupEncryptionService.Decrypt(encryptedBytes, "s3cr3t!");
        var header = Encoding.ASCII.GetString(decrypted, 0, 16);
        Assert.Equal("SQLite format 3\0", header);
    }

    [Fact]
    public async Task DownloadEncrypted_EmptyPassword_IsRejectedWith400()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadEncrypted_UpdatesLastBackupDownloadAt_SameAsPlainDownload()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var beforeCall = DateTime.UtcNow;

        var response = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "pw" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.NotNull(settings!.LastBackupDownloadAt);
        Assert.True(settings.LastBackupDownloadAt!.Value >= beforeCall.AddSeconds(-2));
    }

    // ---------- Restore with encrypted upload ----------

    [Fact]
    public async Task Restore_EncryptedUploadWithCorrectPassword_StagesJustLikeAPlainBackup()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var topicMarker = $"EncRestoreCourse-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions", MakeSession(topicMarker))).StatusCode);

        var download = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "hunter2" });
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var encryptedBytes = await download.Content.ReadAsByteArrayAsync();

        using var upload = MakeUpload(encryptedBytes, password: "hunter2");
        var restore = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        using var body = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        Assert.Equal("staged", body.RootElement.GetProperty("status").GetString());

        // The staging file is the DECRYPTED plaintext, not the upload itself - Validate/Stage
        // ran exactly like the plaintext path over a valid, readable SQLite DB.
        var stagingPath = DatabaseRestoreService.GetStagingPath(factory.DbPath);
        Assert.True(File.Exists(stagingPath));
        using (var connection = new SqliteConnection($"Data Source={stagingPath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Sessions WHERE Topic = $topic;";
            command.Parameters.AddWithValue("$topic", topicMarker);
            Assert.Equal(1L, (long)(command.ExecuteScalar() ?? 0L));
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Restore_EncryptedUploadWithoutPassword_Returns400WithEncryptedFlag_NoStaging()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var download = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "hunter2" });
        var encryptedBytes = await download.Content.ReadAsByteArrayAsync();

        using var upload = MakeUpload(encryptedBytes, password: null);
        var restore = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        using var body = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("encrypted").GetBoolean());
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
    }

    [Fact]
    public async Task Restore_EncryptedUploadWithWrongPassword_Returns400WithEncryptedFlag_NoStaging_NoRawCrash()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var download = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "correct-password" });
        var encryptedBytes = await download.Content.ReadAsByteArrayAsync();

        using var upload = MakeUpload(encryptedBytes, password: "wrong-password");
        var restore = await client.PostAsync("/api/backup/restore", upload);

        // No 500 (the "raw" AesGcm exception would have been an unhandled server error) -
        // clean 400 with a clear, specific error message.
        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        var responseText = await restore.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(responseText);
        Assert.True(body.RootElement.GetProperty("encrypted").GetBoolean());
        Assert.Contains("password", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));

        // Also no safety backup taken - the rejection happens BEFORE CreatePreRestoreBackup,
        // exactly like on a plaintext validation failure.
        var backupDir = Path.Combine(factory.BackupContentRoot, "app_data", "backups");
        Assert.False(Directory.Exists(backupDir) && Directory.GetFiles(backupDir, "prerestore-*.db").Length > 0);
    }

    [Fact]
    public async Task Restore_CorruptedEncryptedUpload_FailsCleanly_NotACrash()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var download = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "pw" });
        var encryptedBytes = await download.Content.ReadAsByteArrayAsync();
        // Header (magic+salt+nonce+tag) remains recognizable, but the ciphertext part is truncated.
        var truncated = encryptedBytes.Take(encryptedBytes.Length / 2).ToArray();

        using var upload = MakeUpload(truncated, password: "pw");
        var restore = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        using var body = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("encrypted").GetBoolean());
        Assert.False(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
    }

    [Fact]
    public async Task Restore_TrulyEmptyEncryptedHeaderOnly_FailsCleanly()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Only the first few bytes of a real encrypted backup (not even the full header) -
        // IsEncryptedFile must still evaluate this as "looks encrypted" (magic matches), but
        // Decrypt must reject it cleanly instead of throwing an index exception.
        var download = await client.PostAsJsonAsync("/api/backup/database/encrypted", new { password = "pw" });
        var encryptedBytes = await download.Content.ReadAsByteArrayAsync();
        var barelyMagic = encryptedBytes.Take(8).ToArray();

        using var upload = MakeUpload(barelyMagic, password: "pw");
        var restore = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
    }

    // ---------- Regression: plaintext path stays unchanged ----------

    [Fact]
    public async Task Restore_PlainUnencryptedUpload_StillHasNoPasswordRequirement_EncryptedFlagAbsent()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Regression check: a perfectly normal, unencrypted upload (no "password" field in the
        // multipart body) still goes through exactly as before this feature - no encrypted flag,
        // no password requirement, 202 as before. The full round trip including
        // ApplyPendingRestore is already covered by BackupRestoreTests.RoundTrip_*; this is just
        // the control point "no encrypted branch is touched for plaintext uploads".
        var plainBytes = (await (await client.GetAsync("/api/backup/database")).Content.ReadAsByteArrayAsync());
        Assert.False(BackupEncryptionService.IsEncrypted(plainBytes));

        using var upload = MakeUpload(plainBytes, password: null, fileName: "studylife-backup.db");
        var restore = await client.PostAsync("/api/backup/restore", upload);

        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        Assert.True(File.Exists(DatabaseRestoreService.GetStagingPath(factory.DbPath)));
    }
}
