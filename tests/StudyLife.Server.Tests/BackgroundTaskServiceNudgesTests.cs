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
/// RunStreakRiskCheckAsync is gated on "late in the day" relative to StudyWindowEndHour (clamped
/// to 18-22h) via DateTime.Now - no injectable clock, same deliberate decision as with
/// WeeklyReport/DailyMotivation. The trigger assertion therefore adapts to the actual
/// execution time.
/// </summary>
public class BackgroundTaskServiceStreakRiskTriggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceStreakRiskTriggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsLateHourGate_FiresWhenStreakAtRisk_AndDedupsSameDay()
    {
        // StudyWindowEndHour=19 -> threshold Clamp(18, 18, 22)=18, the lowest reachable gate.
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.StreakRiskRemindersEnabled = true;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // 3-day streak up until yesterday, nothing yet today -> breaks today if the gate is open.
        for (var daysAgo = 3; daysAgo >= 1; daysAgo--)
        {
            var start = DateTime.Now.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "StreakCourse",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        var now = DateTime.Now;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync();
        if (now.Hour >= 18)
        {
            Assert.Single(sentRows);
            await _service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync());
        }
        else
        {
            Assert.Empty(sentRows);
        }
    }
}

public class BackgroundTaskServiceStreakRiskShortStreakTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceStreakRiskShortStreakTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task StreakBelowTwoDays_NeverFires()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.StreakRiskRemindersEnabled = true;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // Only studied yesterday -> streak 1, below the minimum threshold of 2 - regardless of
        // the hour gate, never a case for firing.
        var start = DateTime.Now.Date.AddDays(-1).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "SingleDay",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync());
    }
}

public class BackgroundTaskServiceStreakRiskToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceStreakRiskToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_NeverFires_EvenWithLongStreak()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.StreakRiskRemindersEnabled = false;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        for (var daysAgo = 5; daysAgo >= 1; daysAgo--)
        {
            var start = DateTime.Now.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "LongStreak",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync());
    }
}

/// <summary>
/// RunWeeklyGoalNudgeCheckAsync is gated on "from Thursday on" (ISO weekday >= 4) via
/// DateTime.Now - no injectable clock, same reasoning as for the other clock-gated checks.
/// </summary>
public class BackgroundTaskServiceWeeklyGoalNudgeTriggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWeeklyGoalNudgeTriggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsThursdayGate_FiresWhenBehindPace_AndDedupsSameWeek()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.WeeklyGoalNudgeEnabled = true;
            s.WeeklyGoalMinHours = 20;
            s.WeeklyGoalMaxHours = 25;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        // No sessions this week -> 0h, guaranteed far below any pace share of 20h/week.

        var now = DateTime.Now;
        var isoDayOfWeek = ((int)now.DayOfWeek + 6) % 7 + 1;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunWeeklyGoalNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklygoalnudge:")).ToListAsync();
        if (isoDayOfWeek >= 4)
        {
            Assert.Single(sentRows);
            await _service.RunWeeklyGoalNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklygoalnudge:")).ToListAsync());
        }
        else
        {
            Assert.Empty(sentRows);
        }
    }
}

public class BackgroundTaskServiceWeeklyGoalNudgeOnPaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWeeklyGoalNudgeOnPaceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task WellAheadOfPace_NeverFires()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.WeeklyGoalNudgeEnabled = true;
            s.WeeklyGoalMinHours = 1;
            s.WeeklyGoalMaxHours = 2;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // 2h recently with a 1h weekly goal: the maximum possible threshold is
        // 1h*1.0*0.5=0.5h - 2h is always above that regardless of weekday/week share. The
        // session is deliberately only a few hours in the past, so it can never fall before the
        // start of the week (Monday), even if the test runs shortly after the week begins.
        var start = DateTime.Now.AddHours(-3);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "AheadOfPace",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(2),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunWeeklyGoalNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklygoalnudge:")).ToListAsync());
    }
}

public class BackgroundTaskServiceWeeklyGoalNudgeToggleDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWeeklyGoalNudgeToggleDisabledTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ToggleDisabled_NeverFires_EvenWithZeroHours()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.WeeklyGoalNudgeEnabled = false;
            s.WeeklyGoalMinHours = 20;
            s.WeeklyGoalMaxHours = 25;
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunWeeklyGoalNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklygoalnudge:")).ToListAsync());
    }
}

/// <summary>
/// RunCourseAlmostDoneCheckAsync is NOT clock-gated (only topic progress + last session),
/// hence fully deterministically testable. Uses a custom study program/course (seeded directly
/// via the DbContext instead of via the StudyPrograms API) so the topic count is freely
/// choosable - the built-in catalog consistently has 4 or 5 topics per course, which cannot hit
/// a percentage in the target window [85, 100) exactly.
/// </summary>
public class BackgroundTaskServiceCourseAlmostDoneTriggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceCourseAlmostDoneTriggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private static async Task<(int ProgramId, int CourseDtoId)> SeedCourseAsync(StudyLifeDb db, string suffix, int topicCount)
    {
        var program = new StudyProgramEntity { Name = $"AlmostDoneProgram{suffix}", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        var topics = string.Join(",", Enumerable.Range(1, topicCount).Select(i => $"T{i}"));
        var course = new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = $"AlmostDoneCourse{suffix}",
            Code = $"ADC-{suffix}",
            Color = "#6C5CE7",
            Icon = "📘",
            Ects = 5,
            Topics = topics,
        };
        db.CustomCourses.Add(course);
        await db.SaveChangesAsync();

        return (program.Id, StudyProgramCatalog.CustomCourseIdOffset + course.Id);
    }

    [Fact]
    public async Task NearCompleteAndStale_FiresAndDedupsSameWeek()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var (programId, courseDtoId) = await SeedCourseAsync(db, "A", 10);

        // 9 of 10 topics checked off (90%) - above the 85% threshold, but not yet done.
        db.CourseGoals.Add(new CourseGoalEntity
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseA",
            CompletedTopics = string.Join(",", Enumerable.Range(1, 9).Select(i => $"T{i}")),
        });
        await db.SaveChangesAsync();

        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseAlmostDoneRemindersEnabled = true;
            s.ActiveStudyProgramId = programId;
            s.SelectedCourseIds = new List<int> { courseDtoId };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // No session for 10 days -> above the 7-day threshold.
        var start = DateTime.Now.AddDays(-10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseA",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        await _service.RunCourseAlmostDoneCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var keyPrefix = $"coursealmostdone:{courseDtoId}:";
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());

        // Second run the same week must not fire a duplicate.
        await _service.RunCourseAlmostDoneCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync());
    }

    [Fact]
    public async Task BelowEightyFivePercent_NeverFires_EvenIfStale()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var (programId, courseDtoId) = await SeedCourseAsync(db, "B", 10);

        // Only 8 of 10 topics (80%) - below the 85% threshold.
        db.CourseGoals.Add(new CourseGoalEntity
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseB",
            CompletedTopics = string.Join(",", Enumerable.Range(1, 8).Select(i => $"T{i}")),
        });
        await db.SaveChangesAsync();

        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseAlmostDoneRemindersEnabled = true;
            s.ActiveStudyProgramId = programId;
            s.SelectedCourseIds = new List<int> { courseDtoId };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var start = DateTime.Now.AddDays(-10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseB",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        await _service.RunCourseAlmostDoneCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith($"coursealmostdone:{courseDtoId}:")).ToListAsync());
    }

    [Fact]
    public async Task RecentSessionWithinSevenDays_NeverFires()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var (programId, courseDtoId) = await SeedCourseAsync(db, "C", 10);

        // 9 of 10 topics (90%) - above the threshold, but the last session was only 2 days ago.
        db.CourseGoals.Add(new CourseGoalEntity
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseC",
            CompletedTopics = string.Join(",", Enumerable.Range(1, 9).Select(i => $"T{i}")),
        });
        await db.SaveChangesAsync();

        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseAlmostDoneRemindersEnabled = true;
            s.ActiveStudyProgramId = programId;
            s.SelectedCourseIds = new List<int> { courseDtoId };
        });
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var start = DateTime.Now.AddDays(-2);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneCourseC",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        await _service.RunCourseAlmostDoneCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith($"coursealmostdone:{courseDtoId}:")).ToListAsync());
    }
}

/// <summary>
/// RunBestStudyTimeCheckAsync is additionally gated on "close to the best 2h bucket" (see
/// BackgroundTaskService.Nudges.cs) via DateTime.Now - no injectable clock. The historical
/// sessions are deliberately placed exactly in the bucket of the current hour, so the
/// determined "best" bucket is always the current one; whether the window (15 min before to
/// 30 min after) is hit exactly still depends on the actual time of day - the trigger assertion
/// therefore replicates the same window formula as the production logic, analogous to the
/// other clock-gated tests above.
/// </summary>
public class BackgroundTaskServiceBestStudyTimeTriggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceBestStudyTimeTriggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task RespectsBucketProximityGate_FiresNearBestBucket_AndDedupsSameDay()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.BestStudyTimeRemindersEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        var now = DateTime.Now;
        var bucketStartHour = now.Hour / 2 * 2;

        // 10 historical sessions, all in the same 2h bucket as the current hour, spread across
        // various past days (never today) - guarantees this bucket becomes the
        // densest/only bucket.
        for (var daysAgo = 1; daysAgo <= 10; daysAgo++)
        {
            var start = now.Date.AddDays(-daysAgo).AddHours(bucketStartHour).AddMinutes(15);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "BestTimeCourse",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunBestStudyTimeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var minutesUntilBucket = bucketStartHour * 60 - (now.Hour * 60 + now.Minute);
        var expectFires = minutesUntilBucket is <= 15 and >= -30;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("beststudytime:")).ToListAsync();
        if (expectFires)
        {
            Assert.Single(sentRows);
            await _service.RunBestStudyTimeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("beststudytime:")).ToListAsync());
        }
        else
        {
            Assert.Empty(sentRows);
        }
    }
}

public class BackgroundTaskServiceBestStudyTimeInsufficientHistoryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceBestStudyTimeInsufficientHistoryTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task FewerThanTenTotalSessions_NeverFires()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.BestStudyTimeRemindersEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // Only 3 completed sessions in total - below the minimum threshold of 10, regardless of
        // time of day never enough history for a reliable bucket statement.
        for (var daysAgo = 1; daysAgo <= 3; daysAgo++)
        {
            var start = DateTime.Now.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "TooFewSessions",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunBestStudyTimeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("beststudytime:")).ToListAsync());
    }
}

public class BackgroundTaskServiceBestStudyTimeAlreadyStudiedTodayTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceBestStudyTimeAlreadyStudiedTodayTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task StudiedToday_NeverFires_RegardlessOfHistory()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.BestStudyTimeRemindersEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));

        // Enough history (10 sessions, not today) ...
        for (var daysAgo = 1; daysAgo <= 10; daysAgo++)
        {
            var start = DateTime.Now.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "EnoughHistory",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }
        // ... but studying already happened today -> the nudge becomes unnecessary, regardless
        // of how close the best bucket is to the current time.
        var todayStart = DateTime.Now.AddHours(-1);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "EnoughHistory",
            CourseColor = "#6C5CE7",
            StartTime = todayStart,
            EndTime = todayStart.AddMinutes(30),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await _service.RunBestStudyTimeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Empty(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("beststudytime:")).ToListAsync());
    }
}

/// <summary>
/// Expired-subscription (410 Gone) handling of RunCourseAlmostDoneCheckAsync: the only nudge in
/// BackgroundTaskService.Nudges.cs without any wall-clock gate, so the removal branch
/// (result.Expired -> Remove + trailing SaveChanges) is fully deterministically testable.
/// GoneEndpoint + FakePushKeys (see BackgroundTaskServiceTestHelpers.cs) drive the REAL
/// WebPush send path into the 410 branch - a placeholder key would already fail during payload
/// encryption and never reach the HTTP roundtrip.
/// </summary>
public class BackgroundTaskServiceCourseAlmostDoneExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceCourseAlmostDoneExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ExpiredSubscription_IsRemoved_AndReminderStillRecorded()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        var program = new StudyProgramEntity { Name = "AlmostDoneGoneProgram", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();
        var course = new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "AlmostDoneGoneCourse",
            Code = "ADG-1",
            Color = "#6C5CE7",
            Icon = "📘",
            Ects = 5,
            Topics = string.Join(",", Enumerable.Range(1, 10).Select(i => $"T{i}")),
        };
        db.CustomCourses.Add(course);
        await db.SaveChangesAsync();
        var courseDtoId = StudyProgramCatalog.CustomCourseIdOffset + course.Id;

        // 9 of 10 topics done (90%, inside [85, 100)) and NO session ever for this course -
        // "never studied" counts as maximally stale (daysSince = int.MaxValue >= 7).
        db.CourseGoals.Add(new CourseGoalEntity
        {
            CourseId = courseDtoId,
            CourseName = "AlmostDoneGoneCourse",
            CompletedTopics = string.Join(",", Enumerable.Range(1, 9).Select(i => $"T{i}")),
        });
        await db.SaveChangesAsync();

        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.CourseAlmostDoneRemindersEnabled = true;
            s.ActiveStudyProgramId = program.Id;
            s.SelectedCourseIds = new List<int> { courseDtoId };
        });

        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        await _service.RunCourseAlmostDoneCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        // Reminder claimed AND the revoked subscription is gone from the DB - proves the
        // Expired branch of the inner send loop plus the trailing SaveChangesAsync ran.
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith($"coursealmostdone:{courseDtoId}:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());
    }
}

/// <summary>
/// Expired-subscription (410) handling of RunStreakRiskCheckAsync. Clock-gated on
/// "hour >= Clamp(StudyWindowEndHour-1, 18, 22)" via DateTime.Now (no injectable clock, see
/// BackgroundTaskServiceStreakRiskTriggerTests) - the expected outcome is therefore computed
/// from the wall clock before AND after the run; if the gate state flips mid-test
/// (18:00/midnight boundary), only the invariant "reminder recorded &lt;=&gt; expired
/// subscription removed" is asserted instead of a fixed outcome.
/// </summary>
public class BackgroundTaskServiceStreakRiskExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceStreakRiskExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    [Fact]
    public async Task ExpiredSubscription_IsRemoved_WhenLateHourGateOpen()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.StreakRiskRemindersEnabled = true;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19; // threshold Clamp(18, 18, 22) = 18, lowest reachable gate
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        for (var daysAgo = 3; daysAgo >= 1; daysAgo--)
        {
            var start = DateTime.Now.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "StreakGone",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var before = DateTime.Now;
        await _service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (before.Hour >= 18 && after.Hour >= 18 && before.Date == after.Date)
        {
            Assert.Single(sentRows);
            Assert.Empty(subRows);
        }
        else if (before.Hour < 18 && after.Hour < 18)
        {
            Assert.Empty(sentRows);
            Assert.Single(subRows);
        }
        else
        {
            Assert.True((sentRows.Count == 1 && subRows.Count == 0) || (sentRows.Count == 0 && subRows.Count == 1),
                "fired => subscription removed, not fired => subscription kept");
        }
    }
}

/// <summary>
/// Full send path of RunWeeklyGoalNudgeCheckAsync including the expired-subscription (410)
/// branch. Clock-gated on "Thursday or later" (ISO weekday >= 4) via DateTime.Now - same
/// adaptive assertion pattern as BackgroundTaskServiceWeeklyGoalNudgeTriggerTests, extended by
/// the before/after guard for a midnight boundary mid-test.
/// </summary>
public class BackgroundTaskServiceWeeklyGoalNudgeExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceWeeklyGoalNudgeExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private static int IsoDayOfWeek(DateTime t) => ((int)t.DayOfWeek + 6) % 7 + 1;

    [Fact]
    public async Task FirePath_RemovesExpiredSubscription_WhenThursdayGateOpen()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.WeeklyGoalNudgeEnabled = true;
            s.WeeklyGoalMinHours = 20;
            s.WeeklyGoalMaxHours = 25;
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);
        // No sessions this week -> 0h studied, always far below 50% of the prorated 20h path
        // (from Thursday 00:00 on, the elapsed fraction is >= 3/7, i.e. expectedSoFar >= 8.5h).

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var before = DateTime.Now;
        await _service.RunWeeklyGoalNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklygoalnudge:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (IsoDayOfWeek(before) >= 4 && IsoDayOfWeek(after) >= 4)
        {
            Assert.Single(sentRows);
            Assert.Empty(subRows);
        }
        else if (IsoDayOfWeek(before) < 4 && IsoDayOfWeek(after) < 4)
        {
            Assert.Empty(sentRows);
            Assert.Single(subRows);
        }
        else
        {
            Assert.True((sentRows.Count == 1 && subRows.Count == 0) || (sentRows.Count == 0 && subRows.Count == 1),
                "fired => subscription removed, not fired => subscription kept");
        }
    }
}

/// <summary>
/// Full send path of RunBestStudyTimeCheckAsync including the expired-subscription (410)
/// branch. The "close to the best bucket" window depends on DateTime.Now (no injectable
/// clock), but unlike BackgroundTaskServiceBestStudyTimeTriggerTests the historical sessions
/// are NOT fixed to the current bucket: the seed bucket is chosen so the current time falls
/// into the send window whenever such a bucket exists at all (15 min before to 30 min after a
/// 2h bucket start covers even-hour minutes 0-30 and odd-hour minutes 45-59, except 23:45+
/// where the "next" bucket 24 doesn't exist) - maximizing the wall-clock range in which the
/// actual send path runs.
/// </summary>
public class BackgroundTaskServiceBestStudyTimeExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceBestStudyTimeExpiredSubscriptionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    /// <summary>Bucket start hour (0, 2, ..., 22) whose send window contains t, or null.</summary>
    private static int? BucketInWindow(DateTime t)
    {
        var nowMinutes = t.Hour * 60 + t.Minute;
        for (var bucketStartHour = 0; bucketStartHour <= 22; bucketStartHour += 2)
        {
            var minutesUntilBucket = bucketStartHour * 60 - nowMinutes;
            if (minutesUntilBucket is <= 15 and >= -30) return bucketStartHour;
        }
        return null;
    }

    [Fact]
    public async Task FirePath_RemovesExpiredSubscription_WhenInsideBucketWindow()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.BestStudyTimeRemindersEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        var seedNow = DateTime.Now;
        // If the current time is inside some bucket's window, seed exactly that bucket so the
        // check fires; otherwise fall back to the current bucket (assertions then expect "no send").
        var bucketStartHour = BucketInWindow(seedNow) ?? seedNow.Hour / 2 * 2;
        for (var daysAgo = 1; daysAgo <= 10; daysAgo++)
        {
            var start = seedNow.Date.AddDays(-daysAgo).AddHours(bucketStartHour).AddMinutes(15);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = "BestTimeGone",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var before = DateTime.Now;
        await _service.RunBestStudyTimeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        bool InSeededWindow(DateTime t) => bucketStartHour * 60 - (t.Hour * 60 + t.Minute) is <= 15 and >= -30;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("beststudytime:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (InSeededWindow(before) && InSeededWindow(after) && before.Date == after.Date)
        {
            Assert.Single(sentRows);
            Assert.Empty(subRows);
        }
        else if (!InSeededWindow(before) && !InSeededWindow(after))
        {
            Assert.Empty(sentRows);
            Assert.Single(subRows);
        }
        else
        {
            Assert.True((sentRows.Count == 1 && subRows.Count == 0) || (sentRows.Count == 0 && subRows.Count == 1),
                "fired => subscription removed, not fired => subscription kept");
        }
    }
}
