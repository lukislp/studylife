using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Postgres-mode behavior of the six raw backup/restore endpoints: DatabaseBackupService/
/// DatabaseRestoreService are only registered in SQLite mode (Program.cs), the controller's
/// optional constructor parameters fall back to null, and every raw endpoint must answer 501
/// BEFORE the owner check (structurally impossible regardless of caller) while pointing at
/// the JSON export as the alternative. GET /api/backup/export itself stays functional - it
/// runs via normal EF queries and is provider-independent. Same factory pattern as
/// SystemCapabilitiesTests.NoRawBackupServicesFactory.
/// </summary>
public class BackupControllerNotImplementedTests : IClassFixture<BackupControllerNotImplementedTests.NoRawBackupFactory>
{
    public class NoRawBackupFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Runs AFTER the base ConfigureServices - also removes the temp-path replacement
            // instances the base class registered, leaving the prod Postgres DI state:
            // no registration at all.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(DatabaseBackupService));
                services.RemoveAll(typeof(DatabaseRestoreService));
            });
        }
    }

    private readonly HttpClient _client;

    public BackupControllerNotImplementedTests(NoRawBackupFactory factory)
        => _client = factory.CreateClient();

    private static async Task AssertNotImplementedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Postgres mode", body);
        Assert.Contains("/api/backup/export", body); // the offered alternative
    }

    [Fact]
    public async Task DownloadDatabase_WithoutRawServices_Returns501()
        => await AssertNotImplementedAsync(await _client.GetAsync("/api/backup/database"));

    [Fact]
    public async Task DownloadDatabaseEncrypted_WithoutRawServices_Returns501()
        => await AssertNotImplementedAsync(await _client.PostAsJsonAsync(
            "/api/backup/database/encrypted", new { Password = "irrelevant" }));

    [Fact]
    public async Task Restore_WithoutRawServices_Returns501()
    {
        using var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]), "file", "backup.db" },
        };
        await AssertNotImplementedAsync(await _client.PostAsync("/api/backup/restore", upload));
    }

    [Fact]
    public async Task RestoreStatus_WithoutRawServices_Returns501()
        => await AssertNotImplementedAsync(await _client.GetAsync("/api/backup/restore/status"));

    [Fact]
    public async Task CancelRestore_WithoutRawServices_Returns501()
        => await AssertNotImplementedAsync(await _client.PostAsync("/api/backup/restore/cancel", null));

    [Fact]
    public async Task RestartToApply_WithoutRawServices_Returns501()
        => await AssertNotImplementedAsync(await _client.PostAsync("/api/backup/restore/restart", null));

    [Fact]
    public async Task JsonExport_WithoutRawServices_StaysAvailable()
    {
        // The documented alternative must genuinely work in this mode.
        var response = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
