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
/// RunPushNotificationsAsync (session reminder). All tests share one factory/DB, but EVERY
/// time explicitly set all relevant settings (toggle + threshold) via PUT, instead of relying
/// on a "still untouched" default state - xUnit does not guarantee execution order within a
/// class, another test in the same class could already have changed the singleton settings row.
/// </summary>
public class BackgroundTaskServicePushNotificationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePushNotificationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private async Task<int> CreateSessionAsync(int courseId, DateTime start, bool isCompleted = false)
    {
        var dto = new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "Push Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = isCompleted,
            TimerModeId = 1,
        };
        var response = await _client.PostAsJsonAsync("/api/sessions", dto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<StudySessionDto>();
        return created!.Id;
    }

    private async Task SubscribeAsync(string endpoint, string p256dh, string auth)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe", new PushSubscribeRequest(endpoint, p256dh, auth));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task InvokeAsync() =>
        _factory.WithDbAsync(db => _service.RunPushNotificationsAsync(db, () => db.PushSubscriptions.ToListAsync()));

    private Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix) =>
        _factory.WithDbAsync(db => db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());

    private Task<List<PushSubscriptionEntity>> GetSubscriptionsAsync(string endpoint) =>
        _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == endpoint).ToListAsync());

    [Fact]
    public async Task DueSession_RecordsSentReminder_AndSecondCallDoesNotDuplicate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = true;
            s.SessionReminderMinutes = "5";
        });
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await SubscribeAsync(endpoint, "p256dh-key-value", "auth-key-value");
        var sessionId = await CreateSessionAsync(301, DateTime.Now.AddMinutes(4));

        // The endpoint is syntactically valid but unreachable - SendPushAsync catches the
        // error per subscription (WebPushClient fails at encryption with the placeholder keys,
        // no actual network access needed). The reminder must still be marked as sent - exactly
        // as the production code comment in BackgroundTaskService.Reminders.cs intends
        // ("even if the subscription was gone").
        await InvokeAsync();

        var key = $"{sessionId}:reminder5";
        var afterFirst = await GetSentRemindersAsync(key);
        Assert.Single(afterFirst);

        await InvokeAsync();
        var afterSecond = await GetSentRemindersAsync(key);
        Assert.Single(afterSecond);
    }

    [Fact]
    public async Task ToggleDisabled_SkipsEntirely()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = false;
            s.SessionReminderMinutes = "5";
        });
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await SubscribeAsync(endpoint, "p256dh-key-value", "auth-key-value");
        var sessionId = await CreateSessionAsync(302, DateTime.Now.AddMinutes(4));

        await InvokeAsync();

        Assert.Empty(await GetSentRemindersAsync($"{sessionId}:reminder5"));
    }

    [Fact]
    public async Task ExpiredSubscription_Returns410_IsRemovedFromDb_ButReminderIsStillRecorded()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = true;
            s.SessionReminderMinutes = "5";
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await SubscribeAsync(gone.Url, p256dh, auth);
        var sessionId = await CreateSessionAsync(303, DateTime.Now.AddMinutes(4));

        await InvokeAsync();

        Assert.Empty(await GetSubscriptionsAsync(gone.Url));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder5"));
    }

    [Fact]
    public async Task StaleReminderKeys_OlderThanTwoDays_AreCleanedUp_ExceptAchievementAndCourseGoalKeys()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = true;
            s.SessionReminderMinutes = "5";
        });
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await SubscribeAsync(endpoint, "p256dh-key-value", "auth-key-value");
        // Cleanup only runs if RunPushNotificationsAsync gets past the early "no session in the
        // window" return (line 25 in BackgroundTaskService.Reminders.cs) - hence a regular
        // due session as trigger.
        await CreateSessionAsync(304, DateTime.Now.AddMinutes(4));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            db.SentReminders.Add(new SentReminderEntity { Key = "999:reminder5", SentAt = DateTime.Now.AddDays(-3) });
            db.SentReminders.Add(new SentReminderEntity { Key = "achievement:hours:25", SentAt = DateTime.Now.AddDays(-30) });
            db.SentReminders.Add(new SentReminderEntity { Key = "coursegoal:999:reminder0d", SentAt = DateTime.Now.AddDays(-30) });
            await db.SaveChangesAsync();
        }

        await InvokeAsync();

        Assert.Empty(await GetSentRemindersAsync("999:reminder5"));
        Assert.Single(await GetSentRemindersAsync("achievement:hours:25"));
        Assert.Single(await GetSentRemindersAsync("coursegoal:999:reminder0d"));
    }

    [Fact]
    public async Task MultipleThresholdsOverdueAtOnce_OnlyOnePushSent_ButAllThresholdsMarkedSent()
    {
        // Simulates poller downtime: the session is already so close that ALL configured
        // thresholds are exceeded simultaneously. Before the fix, this would have triggered up
        // to 5 pushes for the same session in a single tick.
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = true;
            s.SessionReminderMinutes = "60,30,10,5,2";
        });
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await SubscribeAsync(endpoint, "p256dh-key-value", "auth-key-value");
        var sessionId = await CreateSessionAsync(305, DateTime.Now.AddSeconds(30));

        await InvokeAsync();

        // All 5 thresholds are now marked as sent (no re-firing later)...
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder60"));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder30"));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder10"));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder5"));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder2"));

        // ...but a second call must not change anything anymore, because everything is already
        // marked as sent - before the fix, the "mark only, don't send" path for the skipped
        // thresholds would have been easy to get wrong (e.g. accidentally re-marking or
        // forgetting a threshold).
        await InvokeAsync();
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder60"));
        Assert.Single(await GetSentRemindersAsync($"{sessionId}:reminder2"));
    }
}

/// <summary>RunCourseGoalReminderCheckAsync. Same convention as above: every test sets its settings explicitly.</summary>
public class BackgroundTaskServiceCourseGoalReminderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceCourseGoalReminderTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private async Task PutGoalAsync(int courseId, DateTime targetDate)
    {
        var dto = new CourseGoalDto
        {
            CourseId = courseId,
            CourseName = "Goal Test Course",
            TargetDate = targetDate,
            CompletedTopics = "",
        };
        var response = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", dto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SubscribeAsync(string endpoint) =>
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync("/api/push/subscribe", new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"))).StatusCode);

    private Task InvokeAsync() =>
        _factory.WithDbAsync(db => _service.RunCourseGoalReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

    private Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix) =>
        _factory.WithDbAsync(db => db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());

    [Fact]
    public async Task DueGoal_RecordsSentReminder_AndSecondCallDoesNotDuplicate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseGoalRemindersEnabled = true;
            s.CourseGoalReminderDays = "0";
        });
        await SubscribeAsync($"https://push.example.com/{Guid.NewGuid():N}");
        await PutGoalAsync(401, DateTime.Today);

        await InvokeAsync();
        var key = "coursegoal:401:reminder0d";
        Assert.Single(await GetSentRemindersAsync(key));

        await InvokeAsync();
        Assert.Single(await GetSentRemindersAsync(key));
    }

    [Fact]
    public async Task GoalFarInTheFuture_DoesNotFire()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseGoalRemindersEnabled = true;
            s.CourseGoalReminderDays = "14,7,3,1,0";
        });
        await SubscribeAsync($"https://push.example.com/{Guid.NewGuid():N}");
        await PutGoalAsync(402, DateTime.Today.AddDays(30));

        await InvokeAsync();

        Assert.Empty(await GetSentRemindersAsync("coursegoal:402:"));
    }

    [Fact]
    public async Task ToggleDisabled_SkipsEntirely()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseGoalRemindersEnabled = false;
            s.CourseGoalReminderDays = "0";
        });
        await SubscribeAsync($"https://push.example.com/{Guid.NewGuid():N}");
        await PutGoalAsync(403, DateTime.Today);

        await InvokeAsync();

        Assert.Empty(await GetSentRemindersAsync("coursegoal:403:"));
    }
}

/// <summary>
/// RunInactivityReminderCheckAsync queries the ENTIRE sessions sample when firing (regardless
/// of CourseId) - unlike the course/session reminder tests, a unique Id is therefore not
/// sufficient for isolation here, every scenario needs its own, untouched DB.
/// </summary>
public class BackgroundTaskServiceInactivityNoPriorSessionsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceInactivityNoPriorSessionsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private Task InvokeAsync() =>
        _factory.WithDbAsync(db => _service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

    private Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix) =>
        _factory.WithDbAsync(db => db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());

    [Fact]
    public async Task NeverStudied_FiresImmediately_AndSecondCallSameDayDoesNotDuplicate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.InactivityRemindersEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        await InvokeAsync();
        var key = $"inactivity:{DateTime.Now.Date:yyyyMMdd}";
        Assert.Single(await GetSentRemindersAsync(key));

        await InvokeAsync();
        Assert.Single(await GetSentRemindersAsync(key));
    }
}

public class BackgroundTaskServiceInactivityRecentSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceInactivityRecentSessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RecentSession_WithinThreshold_DoesNotFire()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.InactivityRemindersEnabled = true;
            s.InactivityThresholdDays = 5;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        var start = DateTime.Now.AddDays(-1);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 411,
            CourseName = "Recent",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync());
    }
}

public class BackgroundTaskServiceInactivityToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceInactivityToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_SkipsEntirely()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.InactivityRemindersEnabled = false);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunInactivityReminderCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("inactivity:")).ToListAsync());
    }
}

/// <summary>
/// RunPerCourseInactivityCheckAsync queries (like RunInactivityReminderCheckAsync) the ENTIRE
/// sessions sample to detect "active elsewhere anyway" - each scenario therefore needs its
/// own untouched DB rather than just unique IDs.
/// </summary>
public class BackgroundTaskServicePerCourseInactivityNeglectedTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePerCourseInactivityNeglectedTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private Task InvokeAsync() =>
        _factory.WithDbAsync(db => _service.RunPerCourseInactivityCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

    private async Task PostSessionAsync(int courseId, string name, DateTime start) =>
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = name,
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

    private Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix) =>
        _factory.WithDbAsync(db => db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());

    [Fact]
    public async Task ActiveOverall_ButOneCourseStale_FiresOnlyForThatCourse_AndDedupsSameDay()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.PerCourseInactivityRemindersEnabled = true;
            s.InactivityThresholdDays = 5;
            s.SelectedCourseIds = new List<int> { 421, 422 };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // Course 421: still studied yesterday -> not neglected.
        await PostSessionAsync(421, "Aktiv", DateTime.Now.AddDays(-1));
        // Course 422: nothing for 12 days, threshold is 5 -> neglected.
        await PostSessionAsync(422, "Vernachlaessigt", DateTime.Now.AddDays(-12));

        await InvokeAsync();

        Assert.Empty(await GetSentRemindersAsync("courseinactivity:421:"));
        Assert.Single(await GetSentRemindersAsync("courseinactivity:422:"));

        // Second run on the same day must not fire a duplicate.
        await InvokeAsync();
        Assert.Single(await GetSentRemindersAsync("courseinactivity:422:"));
    }
}

/// <summary>
/// Covers exactly the boundary that distinguishes this check from
/// RunInactivityReminderCheckAsync: if the user is NOT more active elsewhere than the neglected
/// course (or the course never started), nothing must fire here - that is the sole
/// responsibility of the global inactivity reminder.
/// </summary>
public class BackgroundTaskServicePerCourseInactivityNotDistinctFromGlobalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePerCourseInactivityNotDistinctFromGlobalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task OnlyOneStaleCourseAndNoOtherActivity_DoesNotFire()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.PerCourseInactivityRemindersEnabled = true;
            s.InactivityThresholdDays = 5;
            s.SelectedCourseIds = new List<int> { 431, 432 };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // Course 431 is the only (and therefore also most recent) session overall - no other
        // activity that would mark this course as "neglected despite other activity".
        var start = DateTime.Now.AddDays(-12);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 431,
            CourseName = "EinzigeSession",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        // Course 432 was never started -> also not a case of "neglected".

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunPerCourseInactivityCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("courseinactivity:")).ToListAsync());
    }
}

public class BackgroundTaskServicePerCourseInactivityToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePerCourseInactivityToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_SkipsEntirely_EvenWithANeglectedCourse()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.PerCourseInactivityRemindersEnabled = false;
            s.InactivityThresholdDays = 5;
            s.SelectedCourseIds = new List<int> { 441, 442 };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var recent = DateTime.Now.AddDays(-1);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 441,
            CourseName = "Aktiv",
            CourseColor = "#6C5CE7",
            StartTime = recent,
            EndTime = recent.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });
        var stale = DateTime.Now.AddDays(-12);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 442,
            CourseName = "Vernachlaessigt",
            CourseColor = "#6C5CE7",
            StartTime = stale,
            EndTime = stale.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunPerCourseInactivityCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("courseinactivity:")).ToListAsync());
    }
}

/// <summary>BuildSessionReminderBody: pure text extension, see BackgroundTaskService.Reminders.cs.</summary>
public class BackgroundTaskServiceSessionReminderResourceLinkTests
{
    [Fact]
    public void NoResources_BodyUnchanged()
    {
        var body = BackgroundTaskService.BuildSessionReminderBody("Analysis 1", "Integrale", new DateTime(2026, 7, 15, 14, 30, 0), resourceCount: 0);

        Assert.Equal("Analysis 1: Integrale um 14:30 Uhr", body);
        Assert.DoesNotContain("Ressource", body);
    }

    [Fact]
    public void OneResource_AppendsSingularSuffix()
    {
        var body = BackgroundTaskService.BuildSessionReminderBody("Analysis 1", null, new DateTime(2026, 7, 15, 14, 30, 0), resourceCount: 1);

        Assert.Equal("Analysis 1 um 14:30 Uhr — 1 Ressource für diesen Kurs hinterlegt", body);
    }

    [Fact]
    public void MultipleResources_AppendsPluralSuffix()
    {
        var body = BackgroundTaskService.BuildSessionReminderBody("Analysis 1", "Integrale", new DateTime(2026, 7, 15, 14, 30, 0), resourceCount: 3);

        Assert.Equal("Analysis 1: Integrale um 14:30 Uhr — 3 Ressourcen für diesen Kurs hinterlegt", body);
    }
}

/// <summary>
/// RunPushNotificationsAsync with course resources on file: smoke test that the full
/// sessions/CourseResources path with the new resourceCountsByCourseId lookup doesn't break and
/// the reminder is still delivered. The actual payload text is NOT verifiable here (WebPushClient
/// encrypts before sending, see the FakePushKeys comment above) - the actual text correctness
/// (singular/plural, no suffix without resources) is covered directly by
/// BackgroundTaskServiceSessionReminderResourceLinkTests via the pure function
/// BuildSessionReminderBody.
/// </summary>
public class BackgroundTaskServicePushNotificationResourceLinkTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServicePushNotificationResourceLinkTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task SessionWithCourseResources_StillFiresReminder_ResourceCountIsPickedUp()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.SessionRemindersEnabled = true;
            s.SessionReminderMinutes = "5";
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        const int courseId = 601;
        await _client.PostAsJsonAsync("/api/courseresources", new CourseResourceDto
        {
            CourseId = courseId,
            Title = "Vorlesungsfolien",
            Url = "https://example.com/slides",
        });
        await _client.PostAsJsonAsync("/api/courseresources", new CourseResourceDto
        {
            CourseId = courseId,
            Title = "Übungsblatt",
            Url = "https://example.com/exercise",
        });

        var start = DateTime.Now.AddMinutes(4);
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "Resource Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = false,
            TimerModeId = 1,
        });
        var sessionId = (await response.Content.ReadFromJsonAsync<StudySessionDto>())!.Id;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunPushNotificationsAsync(db, () => db.PushSubscriptions.ToListAsync());

        var key = $"{sessionId}:reminder5";
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync());
    }
}
