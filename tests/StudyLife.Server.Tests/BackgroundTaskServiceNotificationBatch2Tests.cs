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
/// RunComebackNudgeCheckAsync (feature 1): gentle comeback nudge after EXACTLY 1 day of break -
/// day before yesterday had a session, yesterday didn't, today doesn't (yet). Gated on "late in
/// the day" relative to StudyWindowEndHour (clamped to 18-22h), same threshold
/// RunStreakRiskCheckAsync uses, so a same-day session that simply hasn't happened yet isn't
/// mistaken for "nothing today". No injectable clock, same deliberate decision as
/// WeeklyReport/DailyMotivation/StreakRisk - the trigger assertion below adapts to the actual
/// execution time instead.
/// </summary>
public class BackgroundTaskServiceComebackNudgeTriggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeTriggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsLateHourGate_FiresWhenExactlyOneDayPause_AndDedupsSameDay()
    {
        // StudyWindowEndHour=19 -> threshold Clamp(18, 18, 22)=18, the lowest reachable gate.
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.ComebackNudgeEnabled = true;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var start = DateTime.Now.Date.AddDays(-2).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 601,
            CourseName = "DayBeforeYesterday",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        // yesterday and today intentionally left without a session.

        var now = DateTime.Now;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var key = $"comebacknudge:{DateTime.Now.Date:yyyyMMdd}";
        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync();
        if (now.Hour >= 18)
        {
            Assert.Single(sentRows);
            await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync());
        }
        else
        {
            Assert.Empty(sentRows);
        }
    }
}

/// <summary>
/// Regression test for the actual reported bug: a regular daily studier (session logged
/// yesterday) got told "Kleine Pause gestern" anyway, because the original condition only
/// checked "last past session was yesterday" instead of "yesterday was actually empty". This
/// must never fire regardless of the wall-clock hour the suite runs at - the yesterday-studied
/// check happens independently of the hour gate.
/// </summary>
public class BackgroundTaskServiceComebackNudgeStudiedYesterdayTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeStudiedYesterdayTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task StudiedYesterday_NeverFires_EvenLateInTheDay()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => { s.ComebackNudgeEnabled = true; s.StudyWindowStartHour = 6; s.StudyWindowEndHour = 19; });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 602,
            CourseName = "Yesterday",
            CourseColor = "#6C5CE7",
            StartTime = yesterday,
            EndTime = yesterday.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
    }
}

public class BackgroundTaskServiceComebackNudgeStudiedTodayTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeStudiedTodayTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task StudiedToday_NeverFires()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => { s.ComebackNudgeEnabled = true; s.StudyWindowStartHour = 6; s.StudyWindowEndHour = 19; });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var dayBeforeYesterday = DateTime.Now.Date.AddDays(-2).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 603,
            CourseName = "DayBeforeYesterday",
            CourseColor = "#6C5CE7",
            StartTime = dayBeforeYesterday,
            EndTime = dayBeforeYesterday.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        var todayStart = DateTime.Now.AddMinutes(-5);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 603,
            CourseName = "Today",
            CourseColor = "#6C5CE7",
            StartTime = todayStart,
            EndTime = todayStart.AddMinutes(30),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
    }
}

public class BackgroundTaskServiceComebackNudgeLongerPauseTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeLongerPauseTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task TwoDaysPause_NeverFires_ThatIsInactivityReminderTerritory()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => { s.ComebackNudgeEnabled = true; s.StudyWindowStartHour = 6; s.StudyWindowEndHour = 19; });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var start = DateTime.Now.Date.AddDays(-3).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 604,
            CourseName = "ThreeDaysAgo",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        // day before yesterday, yesterday, and today all intentionally left without a session.

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
    }
}

public class BackgroundTaskServiceComebackNudgeToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_NeverFires_EvenWithExactlyOneDayPause()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => { s.ComebackNudgeEnabled = false; s.StudyWindowStartHour = 6; s.StudyWindowEndHour = 19; });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var start = DateTime.Now.Date.AddDays(-2).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 605,
            CourseName = "DayBeforeYesterday",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        // yesterday and today intentionally left without a session - this would fire if enabled.

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
    }
}

/// <summary>
/// RunMonthlyReportAsync (feature 4) is gated on "1st of the following month, from 9 am local
/// time" via DateTime.Now - no injectable clock, same deliberate decision as with the weekly
/// review (see BackgroundTaskServiceWeeklyReportTests). Assertions adapt to the actual
/// execution time.
/// </summary>
public class BackgroundTaskServiceMonthlyReportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceMonthlyReportTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsFirstOfMonthGate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var now = DateTime.Now;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync();
        if (now.Day == 1 && now.Hour >= 9)
        {
            Assert.Single(sentRows);
            await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
        }
        else
        {
            Assert.Empty(sentRows);
        }
    }

    [Fact]
    public async Task ToggleDisabled_NeverSendsRegardlessOfDay()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = false);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
    }
}
