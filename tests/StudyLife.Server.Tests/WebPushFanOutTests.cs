// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-11-16 audit, M9: Web Push fan-out could stall the shared worker - no cap on registrations
/// per account, the default 100-second HttpClient timeout, and users processed one after another.
/// </summary>
public class WebPushFanOutTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebPushFanOutTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Accepts connections and never answers - a push endpoint that hangs.</summary>
    private sealed class HangingEndpoint : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];

        public HangingEndpoint()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_held) _held.Add(client);
                }
            }
            catch (Exception)
            {
                // listener stopped
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_held) foreach (var c in _held) c.Dispose();
        }
    }

    private static PushSubscriptionEntity Subscription(string endpoint)
    {
        var (p256dh, auth) = FakePushKeys.Generate();
        return new PushSubscriptionEntity { Endpoint = endpoint, P256dh = p256dh, Auth = auth, CreatedAt = DateTime.UtcNow };
    }

    [Fact]
    public async Task AnEndpointThatNeverAnswers_FailsWithinTheRequestTimeout()
    {
        using var hanging = new HangingEndpoint();
        using var delivery = new WebPushDelivery(_factory.Services.GetRequiredService<VapidKeysHolder>(), TimeSpan.FromMilliseconds(500));

        var watch = Stopwatch.StartNew();
        var (outcome, _) = await delivery.SendAsync(Subscription(hanging.Url), "{}", CancellationToken.None);

        Assert.Equal(WebPushOutcome.Failed, outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void TheProductionLimits_AreShort()
    {
        Assert.True(WebPushDelivery.RequestTimeout <= TimeSpan.FromSeconds(10));
        Assert.True(WebPushDelivery.ConnectTimeout <= TimeSpan.FromSeconds(5));
        Assert.True(BackgroundTaskService.DefaultPerUserPushBudget <= TimeSpan.FromSeconds(30));
        // The app shares one transport: DI hands every consumer the same instance.
        Assert.Same(_factory.Services.GetRequiredService<WebPushDelivery>(), _factory.Services.GetRequiredService<WebPushDelivery>());
    }

    /// <summary>One user's dead endpoints may cost the worker at most the per-user budget, then the
    /// rest of that user's deliveries is skipped and the next user's turn comes.</summary>
    [Fact]
    public async Task OneUsersHangingEndpoints_AreCutOffByThePerUserBudget()
    {
        using var hanging = new HangingEndpoint();
        var service = new BackgroundTaskService(
            _factory.Services,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            _factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
            _factory.Services.GetRequiredService<ApnsSender>(),
            webPush: new WebPushDelivery(_factory.Services.GetRequiredService<VapidKeysHolder>(), TimeSpan.FromSeconds(60)))
        {
            PerUserPushBudget = TimeSpan.FromMilliseconds(500),
        };
        var results = new List<BackgroundTaskService.PushSendResult>();
        var laterSubtaskRan = false;
        WorkerSubtask[] table =
        [
            new()
            {
                Name = "slow pushes",
                Scope = WorkerSubtaskScope.PerUser,
                ErrorMessage = "slow pushes failed",
                RunForUser = async (_, _) =>
                {
                    // Sequential on purpose: without the budget this would take 5 x 60 seconds.
                    for (var i = 0; i < 5; i++)
                        results.Add(await service.SendPushAsync(service.GetPushClient(), Subscription(hanging.Url), "{}", "push failed for {Endpoint}"));
                },
            },
            new()
            {
                Name = "later",
                Scope = WorkerSubtaskScope.PerUser,
                ErrorMessage = "later failed",
                RunForUser = (_, _) => { laterSubtaskRan = true; return Task.CompletedTask; },
            },
        ];
        var now = DateTime.UtcNow;
        foreach (var subtask in table) subtask.OpenTick(now);

        var watch = Stopwatch.StartNew();
        await service.RunPerUserSubtasksAsync(table, authUserId: 1, now);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.False(r.Expired)); // skipped, not deleted
        Assert.True(laterSubtaskRan);
    }

    [Fact]
    public async Task Subscribing_BeyondTheCap_ReplacesTheOldestRegistration()
    {
        await _factory.WithDbAsync(async db =>
        {
            await db.PushSubscriptions.IgnoreQueryFilters().Where(s => s.AuthUserId == 1).ExecuteDeleteAsync();
            return 0;
        });
        for (var i = 0; i < PushService.MaxSubscriptionsPerUser + 3; i++)
        {
            var (p256dh, auth) = FakePushKeys.Generate();
            var response = await _client.PostAsJsonAsync("/api/push/subscribe",
                new PushSubscribeRequest($"https://push.example.com/send/{i}", p256dh, auth));
            response.EnsureSuccessStatusCode();
        }

        var endpoints = await _factory.WithDbAsync(db => db.PushSubscriptions.IgnoreQueryFilters()
            .Where(s => s.AuthUserId == 1).Select(s => s.Endpoint).ToListAsync());

        Assert.Equal(PushService.MaxSubscriptionsPerUser, endpoints.Count);
        Assert.DoesNotContain("https://push.example.com/send/0", endpoints); // the oldest went first
        Assert.Contains($"https://push.example.com/send/{PushService.MaxSubscriptionsPerUser + 2}", endpoints);
    }

    [Fact]
    public async Task TheWorker_LoadsOnlyTheNewestRegistrationsUpToTheCap()
    {
        await _factory.WithDbAsync(async db =>
        {
            await db.PushSubscriptions.IgnoreQueryFilters().Where(s => s.AuthUserId == 1).ExecuteDeleteAsync();
            // Rows from before the cap existed, written directly.
            for (var i = 0; i < PushService.MaxSubscriptionsPerUser + 5; i++)
                db.PushSubscriptions.Add(new PushSubscriptionEntity
                {
                    AuthUserId = 1,
                    Endpoint = $"https://push.example.com/legacy/{i}",
                    P256dh = "k",
                    Auth = "a",
                    CreatedAt = DateTime.UtcNow.AddMinutes(i),
                });
            return await db.SaveChangesAsync();
        });
        var service = BackgroundTaskServiceTestFactory.Create(_factory);
        List<PushSubscriptionEntity>? loaded = null;
        WorkerSubtask[] table =
        [
            new()
            {
                Name = "inspect",
                Scope = WorkerSubtaskScope.PerUser,
                ErrorMessage = "inspect failed",
                RunForUser = async (_, getSubscriptions) => loaded = await getSubscriptions(),
            },
        ];
        var now = DateTime.UtcNow;
        table[0].OpenTick(now);

        await service.RunPerUserSubtasksAsync(table, authUserId: 1, now);

        Assert.Equal(PushService.MaxSubscriptionsPerUser, loaded!.Count);
        Assert.DoesNotContain(loaded, s => s.Endpoint == "https://push.example.com/legacy/0");
    }
}
