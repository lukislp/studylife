using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

// ── Feature 2: instant feedback on a new record (SessionsController.CheckNewRecordAsync) ───────
// Runs directly in the Create/Update request handler, not via the BackgroundTaskService polling
// cycle. Record detection ("longest single session so far") compares GLOBALLY across ALL
// sessions in the DB (not per course) - each scenario therefore needs its OWN, untouched
// factory/DB (own class), exactly as with the achievement/inactivity checks in
// BackgroundTaskServiceTests: two scenarios in the same DB would otherwise steal each other's
// "longest session so far". The actual push payload is not verifiable in plaintext
// (WebPushClient encrypts before sending) - all assertions therefore check via the
// dedup key "newrecord:{sessionId}" in SentReminders whether (and how often) a record push
// was triggered.

public class SessionsControllerNewRecordLongerSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordLongerSessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutSettingsAsync(bool newRecordEnabled)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { NewRecordNotificationsEnabled = newRecordEnabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task SubscribeAsync() =>
        (await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value")))
            .EnsureSuccessStatusCode();

    private async Task<StudySessionDto> CreateCompletedAsync(int courseId, DateTime start, TimeSpan duration)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "Record Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    private async Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    [Fact]
    public async Task LongerSessionThanAllPriorOnes_FiresRecordPush()
    {
        await PutSettingsAsync(newRecordEnabled: true);
        await SubscribeAsync();

        // Baseline: a "normal" 1h session, so there's a comparison basis at all
        // (the very first session deliberately doesn't count as a record, see the
        // CheckNewRecordAsync comment).
        await CreateCompletedAsync(1, DateTime.Now.AddDays(-3), TimeSpan.FromHours(1));

        // Significantly longer session -> new record.
        var record = await CreateCompletedAsync(1, DateTime.Now.AddDays(-1), TimeSpan.FromHours(5));

        Assert.Single(await GetSentRemindersAsync($"newrecord:{record.Id}"));
    }
}

public class SessionsControllerNewRecordEditSameSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordEditSameSessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutSettingsAsync(bool newRecordEnabled)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { NewRecordNotificationsEnabled = newRecordEnabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task SubscribeAsync() =>
        (await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value")))
            .EnsureSuccessStatusCode();

    private async Task<StudySessionDto> CreateCompletedAsync(int courseId, DateTime start, TimeSpan duration)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "Record Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    private async Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    [Fact]
    public async Task EditingSameRecordSessionAgain_DoesNotFireTwice()
    {
        await PutSettingsAsync(newRecordEnabled: true);
        await SubscribeAsync();

        await CreateCompletedAsync(1, DateTime.Now.AddDays(-3), TimeSpan.FromHours(1));
        var record = await CreateCompletedAsync(1, DateTime.Now.AddDays(-1), TimeSpan.FromHours(5));
        Assert.Single(await GetSentRemindersAsync($"newrecord:{record.Id}"));

        // Edit the same session afterward (e.g. change the note) - must not trigger a second
        // push for the same session Id, even though it's still a record.
        record.Notes = "Nachträglich bearbeitet";
        var updateResponse = await _client.PutAsJsonAsync($"/api/sessions/{record.Id}", record);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        Assert.Single(await GetSentRemindersAsync($"newrecord:{record.Id}"));
    }
}

public class SessionsControllerNewRecordShorterSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordShorterSessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutSettingsAsync(bool newRecordEnabled)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { NewRecordNotificationsEnabled = newRecordEnabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task SubscribeAsync() =>
        (await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value")))
            .EnsureSuccessStatusCode();

    private async Task<StudySessionDto> CreateCompletedAsync(int courseId, DateTime start, TimeSpan duration)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "No Record Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    private async Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    [Fact]
    public async Task ShorterSessionThanPriorLongest_DoesNotFire()
    {
        await PutSettingsAsync(newRecordEnabled: true);
        await SubscribeAsync();

        await CreateCompletedAsync(1, DateTime.Now.AddDays(-3), TimeSpan.FromHours(4));
        var shorter = await CreateCompletedAsync(1, DateTime.Now.AddDays(-1), TimeSpan.FromHours(1));

        Assert.Empty(await GetSentRemindersAsync($"newrecord:{shorter.Id}"));
    }
}

public class SessionsControllerNewRecordVeryFirstSessionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordVeryFirstSessionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutSettingsAsync(bool newRecordEnabled)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { NewRecordNotificationsEnabled = newRecordEnabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task SubscribeAsync() =>
        (await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value")))
            .EnsureSuccessStatusCode();

    private async Task<StudySessionDto> CreateCompletedAsync(int courseId, DateTime start, TimeSpan duration)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "No Record Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    private async Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    [Fact]
    public async Task VeryFirstSession_NoComparisonBasis_DoesNotFire()
    {
        await PutSettingsAsync(newRecordEnabled: true);
        await SubscribeAsync();

        // Trivial "record" without any comparison basis - deliberately no push, see the
        // CheckNewRecordAsync comment in SessionsController.cs. Own factory/DB, so this is
        // guaranteed to really be the very first session in the database.
        var first = await CreateCompletedAsync(1, DateTime.Now.AddDays(-1), TimeSpan.FromHours(3));

        Assert.Empty(await GetSentRemindersAsync($"newrecord:{first.Id}"));
    }
}

public class SessionsControllerNewRecordToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutSettingsAsync(bool newRecordEnabled)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { NewRecordNotificationsEnabled = newRecordEnabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task SubscribeAsync() =>
        (await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value")))
            .EnsureSuccessStatusCode();

    private async Task<StudySessionDto> CreateCompletedAsync(int courseId, DateTime start, TimeSpan duration)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseId,
            CourseName = "Toggle Disabled Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    private async Task<List<SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    [Fact]
    public async Task ToggleDisabled_NeverFires_EvenWithANewRecord()
    {
        await PutSettingsAsync(newRecordEnabled: false);
        await SubscribeAsync();

        await CreateCompletedAsync(1, DateTime.Now.AddDays(-3), TimeSpan.FromHours(1));
        var record = await CreateCompletedAsync(1, DateTime.Now.AddDays(-1), TimeSpan.FromHours(5));

        Assert.Empty(await GetSentRemindersAsync($"newrecord:{record.Id}"));
    }
}
