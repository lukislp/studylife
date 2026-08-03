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
