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
/// RunDailyMotivationAsync is hard-gated on "from 8 am server time" (DateTime.Now, the same
/// deliberate decision against an injectable clock as with the weekly review). The assertions
/// therefore adapt to the actual execution time, as with the WeeklyReport tests. Own class per
/// scenario, because the dedup key ("dailymotivation:yyyyMMdd") is global per calendar day -
/// two scenarios in the same DB would steal each other's SentReminder row.
/// </summary>
public class BackgroundTaskServiceDailyMotivationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceDailyMotivationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private Task InvokeAsync() =>
        _factory.WithDbAsync(db => _service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));

    private Task<List<SentReminderEntity>> GetSentRemindersAsync() =>
        _factory.WithDbAsync(db => db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync());

    [Fact]
    public async Task RespectsMorningGate_AndSecondCallSameDayDoesNotDuplicate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.DailyMotivationEnabled = true;
            s.MotivationalStyle = "zen";
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var now = DateTime.Now;
        await InvokeAsync();

        if (now.Hour >= 8)
        {
            Assert.Single(await GetSentRemindersAsync());
            await InvokeAsync();
            Assert.Single(await GetSentRemindersAsync());
        }
        else
        {
            Assert.Empty(await GetSentRemindersAsync());
        }
    }
}

public class BackgroundTaskServiceDailyMotivationToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceDailyMotivationToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_NeverSendsRegardlessOfTime()
    {
        // DailyMotivationEnabled stays at the DTO default false - this also covers the fact
        // that the category is opt-in (unlike the other, default-enabled toggles).
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MotivationalStyle = "hype");
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync());
    }
}

/// <summary>
/// Memo catch-up after a restart: the SentReminder key for today already exists in the DB (e.g.
/// written by the previous process), the in-memory memo of the fresh service instance is empty -
/// the DB key must win and NO second push/row may be produced. Time-adaptive like the sibling
/// classes: before 8 am the morning gate returns first, and the pre-seeded row simply stays -
/// the assertion (exactly one row) holds in both windows.
/// </summary>
public class BackgroundTaskServiceDailyMotivationMemoCatchUpTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceDailyMotivationMemoCatchUpTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ExistingDbKey_FreshServiceInstance_DoesNotSendAgain()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.DailyMotivationEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        await _factory.WithDbAsync(async db =>
        {
            db.SentReminders.Add(new SentReminderEntity
            {
                Key = $"dailymotivation:{DateTime.Now:yyyyMMdd}",
                SentAt = DateTime.Now,
            });
            await db.SaveChangesAsync();
        });

        // Twice: the first call catches the memo up from the DB key, the second returns via the
        // memo without a DB query - both must leave exactly the pre-seeded row.
        await _factory.WithDbAsync(db => _service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));
        await _factory.WithDbAsync(db => _service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync()));
    }
}

/// <summary>
/// The claim-lost path of RunDailyMotivationAsync: a competing worker commits the same key
/// between the AnyAsync check and TryClaimReminderAsync. The race is provoked deterministically
/// via the getSubscriptions callback, which runs exactly in that window - the losing side must
/// not send and must leave the competitor's row as the only one. Before 8 am the callback never
/// runs (morning gate), so no row exists at all - both outcomes are asserted time-adaptively.
/// </summary>
public class BackgroundTaskServiceDailyMotivationClaimLostTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceDailyMotivationClaimLostTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task CompetingClaimBetweenCheckAndInsert_LoserDoesNotSend()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.DailyMotivationEnabled = true);
        var beforeHour = DateTime.Now.Hour;

        var callbackRan = false;
        await _factory.WithDbAsync(db => _service.RunDailyMotivationAsync(db, async () =>
        {
            callbackRan = true;
            // Competitor commits the claim first (separate scope = separate DbContext).
            await _factory.WithDbAsync(async competitor =>
            {
                competitor.SentReminders.Add(new SentReminderEntity
                {
                    Key = $"dailymotivation:{DateTime.Now:yyyyMMdd}",
                    SentAt = DateTime.Now,
                });
                await competitor.SaveChangesAsync();
            });
            return new List<PushSubscriptionEntity>
            {
                new() { Endpoint = "https://push.example.com/claim-race", P256dh = "p", Auth = "a" },
            };
        }));

        var rows = await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync());
        if (beforeHour >= 8 && DateTime.Now.Hour >= 8)
        {
            Assert.True(callbackRan);
            Assert.Single(rows); // exactly the competitor's row - the loser added nothing
        }
        else
        {
            Assert.Empty(rows); // morning gate closed - the callback (and the race) never ran
        }
    }
}

/// <summary>
/// Expired-subscription cleanup in RunDailyMotivationAsync: 410 from the APNs stub must remove
/// the subscription and persist that removal. Time-adaptive: before 8 am nothing runs and the
/// subscription must survive.
/// </summary>
public class BackgroundTaskServiceDailyMotivationExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceDailyMotivationExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ExpiredApnsToken_RemovesSubscription_MotivationStaysClaimed()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.DailyMotivationEnabled = true);
        await ApnsSubscriptionSeeder.SeedAsync(_factory, "tok-motivation-expired");
        var service = BackgroundTaskServiceTestFactory.Create(_factory, ApnsStubSender.Create(System.Net.HttpStatusCode.Gone));
        var beforeHour = DateTime.Now.Hour;

        await _factory.WithDbAsync(db => service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));

        var rows = await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync());
        var subs = await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().ToListAsync());
        if (beforeHour >= 8 && DateTime.Now.Hour >= 8)
        {
            Assert.Single(rows);
            Assert.Empty(subs); // expired token removed and removal persisted
        }
        else
        {
            Assert.Empty(rows);
            Assert.Single(subs); // gate closed - nothing sent, nothing removed
        }
    }
}

/// <summary>PickDailyMotivationQuote is deliberately deterministic (date -> quote), so the selection is testable without a clock seam.</summary>
public class BackgroundTaskServiceDailyMotivationQuotePickTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("zen")]
    [InlineData("intense")]
    [InlineData("hype")]
    public void KnownStyle_ReturnsNonEmptyQuote_Deterministically(string style)
    {
        var date = new DateTime(2026, 7, 15);
        var first = BackgroundTaskService.PickDailyMotivationQuote(style, date);
        var second = BackgroundTaskService.PickDailyMotivationQuote(style, date);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void UnknownOrNullStyle_FallsBackToClaudePool()
    {
        var date = new DateTime(2026, 7, 15);
        var claude = BackgroundTaskService.PickDailyMotivationQuote("claude", date);

        Assert.Equal(claude, BackgroundTaskService.PickDailyMotivationQuote("does-not-exist", date));
        Assert.Equal(claude, BackgroundTaskService.PickDailyMotivationQuote(null, date));
    }

    [Fact]
    public void ConsecutiveDays_YieldDifferentQuotes()
    {
        // Pool length > 1 guarantees different indices on consecutive days.
        var day1 = BackgroundTaskService.PickDailyMotivationQuote("zen", new DateTime(2026, 7, 15));
        var day2 = BackgroundTaskService.PickDailyMotivationQuote("zen", new DateTime(2026, 7, 16));

        Assert.NotEqual(day1, day2);
    }
}
