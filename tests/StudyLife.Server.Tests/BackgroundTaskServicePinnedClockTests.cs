using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// BackgroundTaskService now takes an optional TimeProvider seam (constructor's last parameter) -
/// production always resolves TimeProvider.System there, only tests inject a fixed instant. This
/// file exclusively covers the bodies of five wall-clock-gated sub-tasks (weekly report, monthly
/// report, comeback nudge, streak risk, daily motivation) plus the three dispatch-loop catch
/// blocks around them in ExecuteAsync (BackgroundTaskService.cs) - all of which used to be
/// reachable ONLY when the test suite happened to run inside their real send windows (see the
/// sibling *Tests.cs files, which stay gate-adaptive on purpose and are NOT touched here).
///
/// BackgroundTaskServiceTestFactory.Create (BackgroundTaskServiceTestHelpers.cs, read-only) does
/// not accept a TimeProvider, so every service instance in this file is constructed directly via
/// PinnedClock.CreateService below, mirroring that factory's dependency resolution plus the fixed
/// clock.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}

/// <summary>
/// Shared pinned instant + service factory for every class in this file. 2027-08-01 21:00 local
/// is simultaneously a Sunday AND the 1st of the month AND >= every hour threshold used by the
/// five gated sub-tasks under test at once:
///  - weekly report:      DayOfWeek == Sunday &amp;&amp; hour &gt;= 18
///  - monthly report:     Day == 1 &amp;&amp; hour &gt;= 9
///  - daily motivation:   hour &gt;= 8
///  - streak risk/comeback nudge: hour &gt;= Clamp(StudyWindowEndHour-1, 18, 22), which is 20
///    against the DTO default StudyWindowEndHour=21 - all comfortably covered by 21:00.
/// One fixed clock therefore opens every gate at once, so a single constant can seed every test
/// class below without touching per-test time arithmetic. The date is deliberately picked far from
/// any DST transition (those cluster around March/April and October/November in most zones), so
/// TimeZoneInfo.Local.GetUtcOffset(LocalDateTime) is unambiguous regardless of the machine's
/// configured time zone.
/// </summary>
internal static class PinnedClock
{
    public static readonly DateTime LocalDateTime = new(2027, 8, 1, 21, 0, 0);
    public static readonly DateTimeOffset Instant = new(LocalDateTime, TimeZoneInfo.Local.GetUtcOffset(LocalDateTime));

    public static BackgroundTaskService CreateService(CustomWebApplicationFactory factory, ApnsSender? apnsSender = null) => new(
        factory.Services,
        factory.Services.GetRequiredService<VapidKeysHolder>(),
        factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
        apnsSender ?? factory.Services.GetRequiredService<ApnsSender>(),
        backupService: factory.Services.GetRequiredService<DatabaseBackupService>(),
        timeProvider: new FixedTimeProvider(Instant));
}

/// <summary>
/// RunWeeklyReportAsync's non-empty-week branch: real data in "this week" (topCourse selection,
/// positive delta vs. last week) plus expired-subscription cleanup, then the memo dedup (same
/// instance) and DB-key catch-up (fresh instance, simulated restart) paths.
/// </summary>
public class BackgroundTaskServiceWeeklyReportPinnedRichDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceWeeklyReportPinnedRichDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedSundayEvening_SendsRecapWithTopCourseAndDelta_RemovesExpiredSubscription_DedupsViaMemoAndDbKey()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.WeeklyReportEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        async Task SeedSessionAsync(int courseId, string name, DateTime start, double hours) =>
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = courseId,
                CourseName = name,
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(hours),
                IsCompleted = true,
                TimerModeId = 1,
            });

        // Week of the pinned "today" (2027-08-01, Sunday) is Mon 2027-07-26 .. Sun 2027-08-01.
        var weekStart = new DateTime(2027, 7, 26);
        await SeedSessionAsync(1, "TopCourse", weekStart.AddDays(1).AddHours(9), 2); // Tue Jul 27, 2h
        await SeedSessionAsync(1, "TopCourse", weekStart.AddDays(3).AddHours(9), 1); // Thu Jul 29, 1h -> 3h total
        await SeedSessionAsync(2, "OtherCourse", weekStart.AddDays(4).AddHours(9), 1); // Fri Jul 30, 1h
        // Last week (Mon 2027-07-19 .. Sun 2027-07-25): non-zero so the delta branch computes a real value.
        await SeedSessionAsync(1, "TopCourse", weekStart.AddDays(-6).AddHours(9), 2); // Tue Jul 20, 2h

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklyreport:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());

        // Same instance: the in-memory memo short-circuits without a duplicate row.
        await service.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklyreport:")).ToListAsync());

        // Fresh instance (simulated restart, empty memo): the SentReminder DB key wins, the memo
        // just catches up - still no duplicate.
        var freshService = PinnedClock.CreateService(_factory);
        await freshService.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklyreport:")).ToListAsync());
    }
}

/// <summary>RunWeeklyReportAsync's empty-week branch ("0h gelernt" body).</summary>
public class BackgroundTaskServiceWeeklyReportPinnedEmptyWeekTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceWeeklyReportPinnedEmptyWeekTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedSundayEvening_EmptyThisWeek_SendsZeroHoursRecap()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.WeeklyReportEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        // Deliberately no sessions at all -> thisWeek is guaranteed empty, hitting the "0h" branch.

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunWeeklyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("weeklyreport:")).ToListAsync());
    }
}

/// <summary>
/// RunWeeklyReportAsync's lost-claim race: a competing replica commits the SentReminders key
/// between the dedup check and the claim (provoked via the getSubscriptions callback, which runs
/// exactly in that window) - the loser must memoize without sending.
/// </summary>
public class BackgroundTaskServiceWeeklyReportPinnedClaimRaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceWeeklyReportPinnedClaimRaceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedSundayEvening_LostClaimRace_DoesNotSend_MemoizesWithoutSending()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.WeeklyReportEnabled = true);

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        var weekId = $"{System.Globalization.ISOWeek.GetYear(PinnedClock.LocalDateTime)}-W{System.Globalization.ISOWeek.GetWeekOfYear(PinnedClock.LocalDateTime):D2}";
        var key = $"weeklyreport:{weekId}";

        var callbackRan = false;
        await service.RunWeeklyReportAsync(db, async () =>
        {
            callbackRan = true;
            // Competitor commits the claim first (separate scope = separate DbContext).
            await _factory.WithDbAsync(async competitor =>
            {
                competitor.SentReminders.Add(new SentReminderEntity { Key = key, SentAt = DateTime.Now });
                await competitor.SaveChangesAsync();
            });
            return new List<PushSubscriptionEntity>
            {
                new() { Endpoint = "https://push.example.com/weekly-claim-race", P256dh = "p", Auth = "a" },
            };
        });

        Assert.True(callbackRan);
        // Only the competitor's row - the loser added nothing.
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync());
    }
}

/// <summary>
/// RunMonthlyReportAsync with an active custom study program (the "if" branch of the
/// program-aware catalog selection): full body incl. ECTS snapshot, expired-subscription cleanup,
/// memo dedup (same instance) and DB-key catch-up (fresh instance).
/// </summary>
public class BackgroundTaskServiceMonthlyReportPinnedRichDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceMonthlyReportPinnedRichDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedFirstOfMonth_CustomProgram_SendsRecap_RemovesExpiredSubscription_DedupsViaMemoAndDbKey()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        var program = new StudyProgramEntity { Name = "PinnedMonthlyProgram", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();
        var course = new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "PinnedMonthlyCourse",
            Code = "PMC-1",
            Color = "#6C5CE7",
            Icon = "📘",
            Ects = 5,
            Topics = "T1,T2",
        };
        db.CustomCourses.Add(course);
        await db.SaveChangesAsync();
        var courseDtoId = StudyProgramCatalog.CustomCourseIdOffset + course.Id;

        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.MonthlyReportEnabled = true;
            s.ActiveStudyProgramId = program.Id;
            s.SelectedCourseIds = new List<int> { courseDtoId };
            s.CompletedCourseIds = new List<int> { courseDtoId };
        });

        // Report month = July 2027 (the calendar month before the pinned "now", 2027-08-01),
        // prior month = June 2027 - both sums plus the vs.-prior-month delta branch get real data.
        var reportMonthStart = new DateTime(2027, 7, 1);
        foreach (var (monthStart, hours) in new[] { (reportMonthStart, 2), (reportMonthStart.AddMonths(-1), 1) })
        {
            var start = monthStart.AddDays(9).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = courseDtoId,
                CourseName = "PinnedMonthlyCourse",
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(hours),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        var service = PinnedClock.CreateService(_factory);
        await service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());

        // Same instance: in-memory memo short-circuits.
        await service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());

        // Fresh instance (simulated restart, empty memo): the SentReminder DB key wins.
        var freshService = PinnedClock.CreateService(_factory);
        await freshService.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
    }
}

/// <summary>
/// RunMonthlyReportAsync WITHOUT an active study program (the "else" branch: built-in catalog)
/// and with a report month that has zero sessions (the "0h" body branch).
/// </summary>
public class BackgroundTaskServiceMonthlyReportPinnedEmptyMonthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceMonthlyReportPinnedEmptyMonthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedFirstOfMonth_BuiltInCatalog_EmptyReportMonth_SendsZeroHoursRecap()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        // No sessions at all -> report month sum is guaranteed 0h, and no ActiveStudyProgramId
        // set -> the built-in-catalog branch of the ECTS snapshot.

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
    }
}

/// <summary>RunMonthlyReportAsync's lost-claim race, analogous to the weekly report's.</summary>
public class BackgroundTaskServiceMonthlyReportPinnedClaimRaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceMonthlyReportPinnedClaimRaceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedFirstOfMonth_LostClaimRace_DoesNotSend_MemoizesWithoutSending()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = true);

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        var reportMonthStart = new DateTime(PinnedClock.LocalDateTime.Year, PinnedClock.LocalDateTime.Month, 1).AddMonths(-1);
        var key = $"monthlyreport:{reportMonthStart:yyyy-MM}";

        var callbackRan = false;
        await service.RunMonthlyReportAsync(db, async () =>
        {
            callbackRan = true;
            await _factory.WithDbAsync(async competitor =>
            {
                competitor.SentReminders.Add(new SentReminderEntity { Key = key, SentAt = DateTime.Now });
                await competitor.SaveChangesAsync();
            });
            return new List<PushSubscriptionEntity>
            {
                new() { Endpoint = "https://push.example.com/monthly-claim-race", P256dh = "p", Auth = "a" },
            };
        });

        Assert.True(callbackRan);
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync());
    }
}

/// <summary>
/// RunComebackNudgeCheckAsync's full body: exactly 1 day of pause relative to the pinned "today"
/// (2027-08-01), late-hour gate open via the default StudyWindowEndHour (threshold 20, pinned
/// clock is 21:00), expired-subscription cleanup, and the DB-key dedup on a second call.
/// </summary>
public class BackgroundTaskServiceComebackNudgePinnedClockTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceComebackNudgePinnedClockTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedEvening_ExactlyOneDayPause_SendsNudge_RemovesExpiredSubscription_DedupsViaDbKey()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.ComebackNudgeEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        // Exactly 1 day of pause relative to the pinned "today" (2027-08-01): studied the day
        // before yesterday (Jul 30), nothing yesterday (Jul 31) or today (Aug 1).
        var dayBeforeYesterday = PinnedClock.LocalDateTime.Date.AddDays(-2);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "ComebackCourse",
            CourseColor = "#6C5CE7",
            StartTime = dayBeforeYesterday.AddHours(10),
            EndTime = dayBeforeYesterday.AddHours(11),
            IsCompleted = true,
            TimerModeId = 1,
        });

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());

        // Second call: the DB-key dedup must prevent a duplicate send.
        await service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync());
    }
}

/// <summary>
/// RunStreakRiskCheckAsync's full body: a 3-day streak ending yesterday (relative to the pinned
/// "today"), nothing studied yet today -> the streak is at risk, late-hour gate open via the
/// default StudyWindowEndHour (threshold 20, pinned clock is 21:00), expired-subscription cleanup.
/// </summary>
public class BackgroundTaskServiceStreakRiskPinnedClockTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceStreakRiskPinnedClockTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedEvening_StreakAtRisk_SendsWarning_RemovesExpiredSubscription()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.StreakRiskRemindersEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        // 3-day streak ending yesterday (Jul 29-31 relative to the pinned "today" 2027-08-01),
        // nothing today -> CalcStreak == 3, which breaks today unless a session happens.
        for (var daysAgo = 3; daysAgo >= 1; daysAgo--)
        {
            var start = PinnedClock.LocalDateTime.Date.AddDays(-daysAgo).AddHours(10);
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

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("streakrisk:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());
    }
}

/// <summary>RunDailyMotivationAsync's full body plus expired-subscription cleanup.</summary>
public class BackgroundTaskServiceDailyMotivationPinnedClockTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceDailyMotivationPinnedClockTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedMorning_SendsMotivation_RemovesExpiredSubscription()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.DailyMotivationEnabled = true;
            s.MotivationalStyle = "hype";
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        var service = PinnedClock.CreateService(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync());

        Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync());
        Assert.Empty(await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync());
    }
}

/// <summary>
/// RunDailyMotivationAsync's DB-key catch-up path: the SentReminders key for the pinned day
/// already exists (e.g. written by a previous process), a fresh service instance's in-memory memo
/// is empty - the DB key must win and no second row/push may be produced.
/// </summary>
public class BackgroundTaskServiceDailyMotivationPinnedMemoCatchUpTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceDailyMotivationPinnedMemoCatchUpTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedMorning_ExistingDbKey_FreshServiceInstance_CatchesUpMemo_DoesNotSendAgain()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.DailyMotivationEnabled = true);
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", "p256dh-key-value", "auth-key-value"));
        var dayId = $"{PinnedClock.LocalDateTime:yyyyMMdd}";
        await _factory.WithDbAsync(async db =>
        {
            db.SentReminders.Add(new SentReminderEntity { Key = $"dailymotivation:{dayId}", SentAt = DateTime.Now });
            await db.SaveChangesAsync();
        });

        var service = PinnedClock.CreateService(_factory);
        // Twice: the first call catches the memo up from the DB key, the second returns via the
        // memo without a DB query - both must leave exactly the pre-seeded row.
        await _factory.WithDbAsync(db => service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));
        await _factory.WithDbAsync(db => service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync()));

        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("dailymotivation:")).ToListAsync()));
    }
}

/// <summary>RunDailyMotivationAsync's lost-claim race, analogous to the weekly/monthly reports'.</summary>
public class BackgroundTaskServiceDailyMotivationPinnedClaimLostTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackgroundTaskServiceDailyMotivationPinnedClaimLostTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PinnedMorning_CompetingClaimBetweenCheckAndInsert_LoserDoesNotSend()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.DailyMotivationEnabled = true);
        var service = PinnedClock.CreateService(_factory);
        var dayId = $"{PinnedClock.LocalDateTime:yyyyMMdd}";
        var key = $"dailymotivation:{dayId}";

        var callbackRan = false;
        await _factory.WithDbAsync(db => service.RunDailyMotivationAsync(db, async () =>
        {
            callbackRan = true;
            // Competitor commits the claim first (separate scope = separate DbContext).
            await _factory.WithDbAsync(async competitor =>
            {
                competitor.SentReminders.Add(new SentReminderEntity { Key = key, SentAt = DateTime.Now });
                await competitor.SaveChangesAsync();
            });
            return new List<PushSubscriptionEntity>
            {
                new() { Endpoint = "https://push.example.com/motivation-claim-race", P256dh = "p", Auth = "a" },
            };
        }));

        Assert.True(callbackRan);
        Assert.Single(await _factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().Where(r => r.Key == key).ToListAsync()));
    }
}

/// <summary>
/// The three dispatch-loop catch blocks in ExecuteAsync around RunWeeklyReportAsync,
/// RunMonthlyReportAsync and RunDailyMotivationAsync (BackgroundTaskService.cs) are only
/// reachable when BOTH the sub-task's own internal wall-clock gate is open (so the call actually
/// reaches a throwing DB query instead of returning immediately) AND that query throws. This
/// combines the pinned clock (opens all three gates simultaneously, see PinnedClock) with the
/// broken-per-user-scope technique from BackgroundTaskServiceFaultToleranceTests
/// (BackgroundTaskServiceDispatchTests.cs) - reimplemented locally here since that file's
/// provider/scope/logger types are private to its own test class and therefore not reusable
/// across files.
/// </summary>
public class BackgroundTaskServiceDispatchCatchBlocksPinnedClockTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public BackgroundTaskServiceDispatchCatchBlocksPinnedClockTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient(); // host (incl. migration + VAPID keys) must be up before construction
    }

    [Fact]
    public async Task PinnedGatesOpen_BrokenPerUserScope_LogsAllThreeGatedCatchBlocks_AndLoopSurvives()
    {
        var provider = new FirstScopeRealThenBrokenProvider(_factory.Services);
        var logger = new CapturingLogger();
        // Backup source in a nonexistent directory -> RunBackupDumpAsync fails harmlessly in its
        // OWN catch (unrelated to the three catches under test here), same technique as
        // BackgroundTaskServiceFaultToleranceTests.
        var missingRoot = Path.Combine(Path.GetTempPath(), $"studylife-pinned-missing-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"studylife-pinned-broken-backup-{Guid.NewGuid():N}");
        var backup = new DatabaseBackupService(Path.Combine(missingRoot, "never-created.db"), backupRoot);

        var service = new BackgroundTaskService(
            provider,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            logger,
            _factory.Services.GetRequiredService<ApnsSender>(),
            shardClaim: null,
            backupService: backup,
            timeProvider: new FixedTimeProvider(PinnedClock.Instant));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline &&
                   !(logger.Contains("Error in WeeklyReportService")
                     && logger.Contains("Error in MonthlyReportService")
                     && logger.Contains("Error in DailyMotivationService")))
            {
                await Task.Delay(100);
            }

            Assert.Contains(logger.Messages, m => m.Contains("Error in WeeklyReportService"));
            Assert.Contains(logger.Messages, m => m.Contains("Error in MonthlyReportService"));
            Assert.Contains(logger.Messages, m => m.Contains("Error in DailyMotivationService"));
            Assert.False(service.ExecuteTask!.IsCompleted,
                $"loop must keep running after swallowed failures: {service.ExecuteTask.Exception}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            try { Directory.Delete(backupRoot, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    private sealed class FirstScopeRealThenBrokenProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly IServiceProvider _real;
        private int _scopeCount;

        public FirstScopeRealThenBrokenProvider(IServiceProvider real) => _real = real;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : _real.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            var n = Interlocked.Increment(ref _scopeCount);
            return n == 1
                ? _real.GetRequiredService<IServiceScopeFactory>().CreateScope()
                : new BrokenScope();
        }
    }

    /// <summary>Scope whose StudyLifeDb resolves fine (resolution happens outside the per-task
    /// try blocks) but whose every query throws, because the SQLite file lives in a directory
    /// that doesn't exist.</summary>
    private sealed class BrokenScope : IServiceScope, IServiceProvider
    {
        private readonly StudyLifeDb _db;

        public BrokenScope()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"studylife-pinned-broken-{Guid.NewGuid():N}", "broken.db");
            var options = new DbContextOptionsBuilder<StudyLifeDb>()
                .UseSqlite($"Data Source={missing}")
                .Options;
            _db = new StudyLifeDb(options, new CurrentUserAccessor(new HttpContextAccessor()));
        }

        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(StudyLifeDb) ? _db : null;
        public void Dispose() => _db.Dispose();
    }

    /// <summary>Minimal in-memory ILogger spy - just enough to assert which per-task error
    /// messages ExecuteAsync's catch blocks actually logged.</summary>
    private sealed class CapturingLogger : ILogger<BackgroundTaskService>
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public bool Contains(string substring)
        {
            lock (_messages) return _messages.Any(m => m.Contains(substring));
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_messages) _messages.Add(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
