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
                CourseId = 501,
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
            CourseId = 502,
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
                CourseId = 503,
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
            CourseId = 511,
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
                CourseId = 521,
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
                CourseId = 522,
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
                CourseId = 523,
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
            CourseId = 523,
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
