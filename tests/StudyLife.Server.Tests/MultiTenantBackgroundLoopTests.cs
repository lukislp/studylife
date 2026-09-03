using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Regression coverage for the new user loop in BackgroundTaskService.ExecuteAsync: in today's
/// normal case of EXACTLY ONE AuthUser, the first tick must show exactly the same observable
/// behavior as before the multi-tenant rework (one inactivity reminder, no duplicates) - and the
/// written SentReminder row must be assigned to that one user. Own factory/DB as in
/// BackgroundTaskServiceExecuteAsyncTests, because the tick also triggers VACUUM and the backup
/// dump.
/// </summary>
public class MultiTenantBackgroundLoopTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MultiTenantBackgroundLoopTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ExecuteAsync_WithExactlyOneUser_BehavesLikeBefore_AndStampsTheUsersId()
    {
        // Invalid endpoint as in BackgroundTaskServiceExecuteAsyncTests: fails immediately
        // without network access, the reminder must still be recorded as sent.
        // Inserted directly: PushController.Subscribe now rejects non-https endpoints
        // (OutboundUrlPolicy), so the deliberately broken endpoint has to bypass the API.
        await _factory.WithDbAsync(db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                AuthUserId = 1,
                Endpoint = "this is not a url",
                P256dh = "p256dh-key-value",
                Auth = "auth-key-value",
                CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChangesAsync();
        });

        var service = BackgroundTaskServiceTestFactory.Create(_factory);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        try
        {
            List<SentReminderEntity> inactivityRows = new();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                inactivityRows = await _factory.WithDbAsync(db => db.SentReminders.AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(r => r.Key.StartsWith("inactivity:"))
                    .ToListAsync());
                if (inactivityRows.Count > 0) break;
                await Task.Delay(100);
            }

            // Exactly ONE reminder (identical to the behavior before the rework - the user loop
            // must not execute anything twice for a single user) ...
            var row = Assert.Single(inactivityRows);

            // ... and it carries the AuthUserId of the one existing user, because the loop
            // set the AsyncLocal context before the unchanged check logic ran.
            var userId = await _factory.WithDbAsync(db => db.AuthUsers.Select(u => u.Id).SingleAsync());
            Assert.Equal(userId, row.AuthUserId);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
