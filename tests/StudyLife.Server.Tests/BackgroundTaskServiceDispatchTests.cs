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

/// <summary>
/// Covers the ExecuteAsync dispatch loop itself (scope creation, subscriptions memoization,
/// the gate booleans and their finally blocks). All _next*Run fields start at DateTime.MinValue,
/// so the very first tick invariably runs all nine Run*Async methods - that makes a single
/// awaited tick meaningful, without having to wait out the real 30s interval. Own factory/DB,
/// because a real VACUUM and backup dump operation runs here.
/// </summary>
public class BackgroundTaskServiceExecuteAsyncTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceExecuteAsyncTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task StartAsync_FirstTick_RunsAllGatedSubTasksImmediately()
    {
        // Deliberately a syntactically invalid endpoint (instead of a valid but unreachable
        // one) - fails immediately without network access (see
        // BackgroundTaskServicePushNotificationTests), so that this integration test doesn't
        // depend on network timeouts.
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest("this is not a url", "p256dh-key-value", "auth-key-value"));

        var service = BackgroundTaskServiceTestFactory.Create(_factory);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        try
        {
            // No sessions/course-goal record present -> RunInactivityReminderCheckAsync fires
            // ("never studied yet") and is thus a reliable signal that the first tick has run,
            // without having to query any internal state of the service.
            List<SentReminderEntity> inactivityRows = new();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
                inactivityRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync();
                if (inactivityRows.Count > 0) break;
                await Task.Delay(100);
            }
            Assert.NotEmpty(inactivityRows);

            // RunBackupDumpAsync + RunDatabaseMaintenanceAsync also already run in the first
            // tick (_nextBackupDumpRun/_nextDatabaseMaintenanceRun start at DateTime.MinValue).
            var backupDir = Path.Combine(_factory.BackupContentRoot, "app_data", "backups");
            string[] files = Array.Empty<string>();
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (Directory.Exists(backupDir))
                {
                    files = Directory.GetFiles(backupDir, "studylife-*.db");
                    if (files.Length > 0) break;
                }
                await Task.Delay(100);
            }
            Assert.NotEmpty(files);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
