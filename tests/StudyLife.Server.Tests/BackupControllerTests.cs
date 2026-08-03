using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// CustomWebApplicationFactory already reroutes DatabaseBackupService to DbPath/BackupContentRoot
/// (temp directories) - so backup tests never land in the real app_data.
/// </summary>
public class BackupControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BackupControllerTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task GetDatabase_ReturnsDownloadableAndOpenableSqliteFile()
    {
        var response = await _client.GetAsync("/api/backup/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.NotNull(disposition.FileName);
        Assert.Matches(@"^""?studylife-backup-\d{8}\.db""?$", disposition.FileName!);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 16, "Response is too short for a SQLite header.");

        // SQLite file format magic header, see https://www.sqlite.org/fileformat.html#the_database_header
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, 16);
        Assert.Equal("SQLite format 3\0", header);

        // Not just the header needs to be right - the file must also actually be openable as
        // a consistent, queryable DB via the online backup API (WAL safety).
        var tempFile = Path.Combine(Path.GetTempPath(), $"studylife-backup-verify-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(tempFile, bytes);
            using var connection = new SqliteConnection($"Data Source={tempFile};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Sessions';";
            var tableCount = (long)(command.ExecuteScalar() ?? 0L);
            Assert.Equal(1, tableCount);

            using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", (string)(integrityCommand.ExecuteScalar() ?? ""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(tempFile); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task GetDatabase_UpdatesSettingsLastBackupDownloadAt()
    {
        var beforeCall = DateTime.UtcNow;

        var downloadResponse = await _client.GetAsync("/api/backup/database");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        var settingsResponse = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var settings = await settingsResponse.Content.ReadFromJsonAsync<UserSettingsDto>();

        Assert.NotNull(settings);
        Assert.NotNull(settings!.LastBackupDownloadAt);
        // UTC timestamp, set between beforeCall and "now" (with some buffer for test runtime).
        Assert.True(settings.LastBackupDownloadAt!.Value >= beforeCall.AddSeconds(-2));
        Assert.True(settings.LastBackupDownloadAt!.Value <= DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task Export_IncludesCreatedSessionAndExcludesTransientData()
    {
        var uniqueCourseName = $"BackupExportTestCourse-{Guid.NewGuid():N}";
        var session = new StudySessionDto
        {
            CourseId = 999,
            CourseName = uniqueCourseName,
            StartTime = new DateTime(2026, 8, 1, 10, 0, 0),
            EndTime = new DateTime(2026, 8, 1, 11, 0, 0),
            Topic = "Export-Test",
            IsCompleted = false,
            TimerModeId = 1,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", session);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var exportResponse = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("application/json", exportResponse.Content.Headers.ContentType?.MediaType);

        var disposition = exportResponse.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Matches(@"^""?studylife-export-\d{8}\.json""?$", disposition!.FileName!);

        var json = await exportResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Documented export structure: exportedAt + sessions/notes/courseGoals/settings.
        Assert.True(root.TryGetProperty("exportedAt", out var exportedAtProp));
        var exportedAt = exportedAtProp.GetDateTime();
        Assert.True(exportedAt >= DateTime.UtcNow.AddMinutes(-2));

        Assert.True(root.TryGetProperty("sessions", out var sessionsProp));
        Assert.True(root.TryGetProperty("notes", out _));
        Assert.True(root.TryGetProperty("courseGoals", out _));
        Assert.True(root.TryGetProperty("settings", out _));

        // Deliberately excluded per the BackupController docs: PushSubscriptions/SentReminders/TimerState.
        Assert.False(root.TryGetProperty("pushSubscriptions", out _));
        Assert.False(root.TryGetProperty("sentReminders", out _));
        Assert.False(root.TryGetProperty("timerState", out _));

        var matchingSessions = sessionsProp.EnumerateArray()
            .Where(s => s.GetProperty("CourseName").GetString() == uniqueCourseName)
            .ToList();
        Assert.Single(matchingSessions);
        Assert.Equal("Export-Test", matchingSessions[0].GetProperty("Topic").GetString());
    }
}

/// <summary>
/// Security regression test: the raw database endpoints (download/restore/restart) may
/// ONLY be called by the first registered user (owner) - unlike the rest of the app, they
/// operate on the SQLite file itself instead of via the filtered EF queries, a second/third
/// account could otherwise pull the data of EVERY other user or replace the entire DB
/// (see BackupController.IsOwnerAsync). Own factory (fresh DB), because a real
/// two-user situation is needed via the passkey registration flow.
/// </summary>
public class BackupControllerOwnerRestrictionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackupControllerOwnerRestrictionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RawDatabaseEndpoints_ForSecondRegisteredUser_AreForbidden_ButOwnerStillWorks()
    {
        using var firstKey = new FakePasskey();
        var ownerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var nonOwnerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string token)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Session-Token", token);
            return (await _client.SendAsync(request)).StatusCode;
        }

        // Second user: rejected on all raw DB endpoints.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Get, "/api/backup/database", nonOwnerToken));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Get, "/api/backup/restore/status", nonOwnerToken));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/backup/restore/cancel", nonOwnerToken));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/backup/restore/restart", nonOwnerToken));
        using (var encryptedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/backup/database/encrypted"))
        {
            encryptedRequest.Headers.Add("X-Session-Token", nonOwnerToken);
            encryptedRequest.Content = JsonContent.Create(new { Password = "irrelevant" });
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(encryptedRequest)).StatusCode);
        }
        using (var restoreRequest = new HttpRequestMessage(HttpMethod.Post, "/api/backup/restore"))
        {
            restoreRequest.Headers.Add("X-Session-Token", nonOwnerToken);
            using var upload = new MultipartFormDataContent
            {
                { new ByteArrayContent([1, 2, 3]), "file", "fake.db" },
            };
            restoreRequest.Content = upload;
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(restoreRequest)).StatusCode);
        }

        // Control: the owner (first registered user) may still access normally.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/api/backup/database", ownerToken));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/api/backup/restore/status", ownerToken));

        // GET /api/backup/export stays reachable for EVERY logged-in user (runs through the
        // normal query filters, so it only returns the caller's own data anyway).
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/api/backup/export", nonOwnerToken));
    }
}
