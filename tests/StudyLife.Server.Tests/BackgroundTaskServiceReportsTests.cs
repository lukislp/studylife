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
/// RunAchievementCheckAsync sums hours/sessions/streak over ALL sessions in the DB - each
/// scenario therefore needs (as with inactivity) its own untouched DB instead of just unique ids.
/// </summary>
public class BackgroundTaskServiceAchievementCrossesThresholdTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceAchievementCrossesThresholdTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task TotalHoursCrossing25_FiresHoursAchievement_AndSecondCallDoesNotDuplicate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.AchievementNotificationsEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        // Two sessions instead of a single 29h session (SessionsController.Validate() no longer
        // allows such a long single session since the 24h plausibility limit) - together
        // still 29h, crossing the 25h threshold just the same.
        var start1 = DateTime.Now.AddHours(-40);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 421,
            CourseName = "Long Session 1",
            CourseColor = "#6C5CE7",
            StartTime = start1,
            EndTime = start1.AddHours(15),
            IsCompleted = true,
            TimerModeId = 1,
        });
        var start2 = DateTime.Now.AddHours(-20);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 421,
            CourseName = "Long Session 2",
            CourseColor = "#6C5CE7",
            StartTime = start2,
            EndTime = start2.AddHours(14), // together 29h -> crosses the 25h threshold
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        Func<Task<List<PushSubscriptionEntity>>> getSubs = () => db.PushSubscriptions.ToListAsync();

        await _service.RunAchievementCheckAsync(db, getSubs);
        var afterFirst = await db.SentReminders.AsNoTracking().Where(r => r.Key == "achievement:hours:25").ToListAsync();
        Assert.Single(afterFirst);

        await _service.RunAchievementCheckAsync(db, getSubs);
        var afterSecond = await db.SentReminders.AsNoTracking().Where(r => r.Key == "achievement:hours:25").ToListAsync();
        Assert.Single(afterSecond);
    }
}

public class BackgroundTaskServiceAchievementBelowThresholdTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceAchievementBelowThresholdTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task NoThresholdCrossed_DoesNotFire()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.AchievementNotificationsEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        var start = DateTime.Now.AddHours(-2);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 422,
            CourseName = "Short Session",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1), // only 1h -> below every achievement threshold
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunAchievementCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("achievement:")).ToListAsync());
    }
}

public class BackgroundTaskServiceAchievementToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceAchievementToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_SkipsEvenWhenThresholdWouldBeCrossed()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.AchievementNotificationsEnabled = false);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        // Two sessions instead of one 29h session, see the comment in
        // TotalHoursCrossing25_FiresHoursAchievement_AndSecondCallDoesNotDuplicate above.
        var start1 = DateTime.Now.AddHours(-40);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 423,
            CourseName = "Long Session 1",
            CourseColor = "#6C5CE7",
            StartTime = start1,
            EndTime = start1.AddHours(15),
            IsCompleted = true,
            TimerModeId = 1,
        });
        var start2 = DateTime.Now.AddHours(-20);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 423,
            CourseName = "Long Session 2",
            CourseColor = "#6C5CE7",
            StartTime = start2,
            EndTime = start2.AddHours(14),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunAchievementCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("achievement:")).ToListAsync());
    }
}

/// <summary>
/// The custom-study-program branch of RunAchievementCheckAsync: with an active study program,
/// the ECTS totals come from StudyProgramCatalog (custom courses/quotas) instead of the built-in
/// catalog - observable because "all courses done" is reachable with a single completed custom
/// course, which would be impossible against the built-in catalog's total.
/// </summary>
public class BackgroundTaskServiceAchievementCustomProgramTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceAchievementCustomProgramTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ActiveStudyProgram_AllCustomCoursesCompleted_FiresAllCoursesAchievement()
    {
        // Program with exactly one 5-ECTS mandatory course - completing it means 5/5 ECTS.
        var customCourseId = await _factory.WithDbAsync(async db =>
        {
            var program = new StudyProgramEntity { Name = "Mini Program", CreatedAt = DateTime.UtcNow };
            db.StudyPrograms.Add(program);
            await db.SaveChangesAsync();
            var course = new CustomCourseEntity { StudyProgramId = program.Id, Name = "Einziger Kurs", Ects = 5 };
            db.CustomCourses.Add(course);
            await db.SaveChangesAsync();
            return (ProgramId: program.Id, CourseId: course.Id);
        });
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.AchievementNotificationsEnabled = true;
            s.ActiveStudyProgramId = customCourseId.ProgramId;
            // DTO ids of custom courses are shifted by the catalog offset (see StudyProgramCatalog).
            s.CompletedCourseIds = new List<int> { StudyProgramCatalog.CustomCourseIdOffset + customCourseId.CourseId };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        await _factory.WithDbAsync(db => _service.RunAchievementCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

        var sentKeys = await _factory.WithDbAsync(db => db.SentReminders.AsNoTracking()
            .Where(r => r.Key.StartsWith("achievement:")).Select(r => r.Key).ToListAsync());
        // Proves the custom catalog was used: against the built-in catalog a single completed
        // course id could never reach the full ECTS total.
        Assert.Contains("achievement:allcourses", sentKeys);
        Assert.Contains("achievement:courses:1", sentKeys);
    }
}

/// <summary>
/// Expired-subscription cleanup in RunAchievementCheckAsync: a 410 from the APNs stub must
/// remove the subscription and persist the removal, while the achievement stays claimed.
/// </summary>
public class BackgroundTaskServiceAchievementExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceAchievementExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task EarnedAchievement_ExpiredApnsToken_RemovesSubscription_AchievementStaysClaimed()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.AchievementNotificationsEnabled = true;
            s.CompletedCourseIds = new List<int> { 5 }; // built-in course -> "first course done"
        });
        await ApnsSubscriptionSeeder.SeedAsync(_factory, "tok-achievement-expired");
        var service = BackgroundTaskServiceTestFactory.Create(_factory, ApnsStubSender.Create(System.Net.HttpStatusCode.Gone));

        await _factory.WithDbAsync(db => service.RunAchievementCheckAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key == "achievement:courses:1").ToListAsync()));
        Assert.Empty(await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().ToListAsync()));
    }
}

/// <summary>
/// RunWeeklyReportAsync is hard-gated on "Sunday from 6 PM server time" (DateTime.Now, no
/// injectable clock available - see the report for the deliberate decision not to
/// introduce a new test seam into production logic for this). The tests therefore adjust
/// their assertion to the actual execution time: outside the window, only the gate's
/// early return is checked; inside the window (the rare case that the suite happens to run
/// on a Sunday evening), additionally the full send path including the SentReminder entry.
/// </summary>
public class BackgroundTaskServiceWeeklyReportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWeeklyReportTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsSundayEveningGate()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.WeeklyReportEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var now = DateTime.Now;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklyreport:")).ToListAsync();
        if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 18)
            Assert.Single(sentRows);
        else
            Assert.Empty(sentRows);
    }

    [Fact]
    public async Task ToggleDisabled_NeverSendsRegardlessOfDay()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.WeeklyReportEnabled = false);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        // Before/after instead of Assert.Empty: if the suite runs on a Sunday evening (window
        // open), RespectsSundayEveningGate has already legitimately sent a report in the same
        // class DB - this test only checks that with the toggle disabled NO NEW
        // entry is added (noticed live on Sunday evening 2026-07-19, never hit before).
        var beforeCount = await db.SentReminders.AsNoTracking()
            .CountAsync(r => r.Key.StartsWith("weeklyreport:"));

        await _service.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Equal(beforeCount, await db.SentReminders.AsNoTracking()
            .CountAsync(r => r.Key.StartsWith("weeklyreport:")));
    }
}
