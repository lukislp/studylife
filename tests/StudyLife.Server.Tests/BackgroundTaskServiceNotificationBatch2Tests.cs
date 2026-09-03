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
            CourseId = 1,
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
            CourseId = 1,
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
            CourseId = 1,
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
            CourseId = 1,
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
            CourseId = 1,
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
            CourseId = 1,
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

/// <summary>
/// Expired-subscription (410) handling of RunComebackNudgeCheckAsync. Same "late in the day"
/// clock gate as the trigger test above (no injectable clock) - the expected outcome is
/// computed from the wall clock before AND after the run; if the gate state flips mid-test
/// (18:00/midnight boundary), only the invariant "reminder recorded &lt;=&gt; expired
/// subscription removed" is asserted. GoneEndpoint + FakePushKeys (see
/// BackgroundTaskServiceTestHelpers.cs) drive the real WebPush send path into the 410 branch.
/// </summary>
public class BackgroundTaskServiceComebackNudgeExpiredSubscriptionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceComebackNudgeExpiredSubscriptionTests(CustomWebApplicationFactory factory)
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
            s.ComebackNudgeEnabled = true;
            s.StudyWindowStartHour = 6;
            s.StudyWindowEndHour = 19; // threshold Clamp(18, 18, 22) = 18, lowest reachable gate
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        // Exactly 1 day of pause: session the day before yesterday, nothing yesterday/today.
        var start = DateTime.Now.Date.AddDays(-2).AddHours(10);
        await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "ComebackGone",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start.AddHours(1),
            IsCompleted = true,
            TimerModeId = 1,
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var before = DateTime.Now;
        await _service.RunComebackNudgeCheckAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("comebacknudge:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (before.Hour >= 18 && after.Hour >= 18 && before.Date == after.Date && before.Date == start.Date.AddDays(2))
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
/// Full body of RunMonthlyReportAsync (data aggregation, custom-program ECTS branch,
/// expired-subscription removal, memo + DB-key dedup). Only reachable on the 1st of a month
/// from 9 AM local time (DateTime.Now, no injectable clock, see
/// BackgroundTaskServiceMonthlyReportTests) - on all other days the assertions degrade to
/// "nothing sent, subscription kept", and the rich branches run on the next 1st-of-month CI run.
/// </summary>
public class BackgroundTaskServiceMonthlyReportRichDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceMonthlyReportRichDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private static bool GateOpen(DateTime t) => t.Day == 1 && t.Hour >= 9;

    [Fact]
    public async Task OnFirstOfMonth_SendsRecap_RemovesExpiredSubscription_AndDedupsViaMemoAndDbKey()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        // Custom study program with a completed course -> exercises the program-aware catalog
        // branch (StudyProgramCatalog.LoadCoursesAsync/LoadGroupQuotasAsync) and the
        // CompletedCourseIds parsing of the ECTS snapshot.
        var program = new StudyProgramEntity { Name = "MonthlyRichProgram", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();
        var course = new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "MonthlyRichCourse",
            Code = "MRC-1",
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

        // Sessions in the report month (previous calendar month) AND the month before it ->
        // both sums plus the vs.-prior-month delta branch have real data.
        var reportMonthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(-1);
        foreach (var (monthStart, hours) in new[] { (reportMonthStart, 2), (reportMonthStart.AddMonths(-1), 1) })
        {
            var start = monthStart.AddDays(9).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = courseDtoId,
                CourseName = "MonthlyRichCourse",
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

        var before = DateTime.Now;
        await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (GateOpen(before) && GateOpen(after))
        {
            Assert.Single(sentRows);
            Assert.Empty(subRows);

            // Second call on the SAME instance: the in-memory memo short-circuits.
            await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());

            // Fresh instance (simulated restart, empty memo): the SentReminder DB key wins and
            // the memo just catches up - still no duplicate.
            var freshService = BackgroundTaskServiceTestFactory.Create(_factory);
            await freshService.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
            Assert.Single(await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync());
        }
        else if (!GateOpen(before) && !GateOpen(after))
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
/// Zero-hours branch of RunMonthlyReportAsync ("Im X 0h gelernt ...", reportMonth.Count == 0)
/// with the built-in catalog (no ActiveStudyProgramId). Same 1st-of-month gate handling as
/// BackgroundTaskServiceMonthlyReportRichDataTests.
/// </summary>
public class BackgroundTaskServiceMonthlyReportEmptyMonthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceMonthlyReportEmptyMonthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private static bool GateOpen(DateTime t) => t.Day == 1 && t.Hour >= 9;

    [Fact]
    public async Task EmptyReportMonth_SendsZeroHoursRecap_AndRemovesExpiredSubscription()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);
        // Deliberately no sessions at all -> the report month sum is guaranteed 0h.

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var before = DateTime.Now;
        await _service.RunMonthlyReportAsync(db, () => db.PushSubscriptions.ToListAsync());
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (GateOpen(before) && GateOpen(after))
        {
            Assert.Single(sentRows);
            Assert.Empty(subRows);
        }
        else if (!GateOpen(before) && !GateOpen(after))
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
/// Lost claim race of RunMonthlyReportAsync: TryClaimReminderAsync returns false (another
/// replica committed the same key between the dedup check and the claim), the loser memoizes
/// and aborts BEFORE sending. The race is provoked deterministically via the getSubscriptions
/// callback, which runs exactly between the SentReminders dedup check and the claim: it inserts
/// the key through a SEPARATE DbContext, so the service's own claim insert then violates the
/// unique (AuthUserId, Key) index. Only meaningful on the 1st of a month from 9 AM (see gate
/// comment on the sibling classes); on other days the callback is never invoked.
/// </summary>
public class BackgroundTaskServiceMonthlyReportClaimRaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly BackgroundTaskService _service;

    public BackgroundTaskServiceMonthlyReportClaimRaceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _service = BackgroundTaskServiceTestFactory.Create(factory);
    }

    private static bool GateOpen(DateTime t) => t.Day == 1 && t.Hour >= 9;

    [Fact]
    public async Task LostClaimRace_DoesNotSend_AndKeepsSubscription()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.MonthlyReportEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var key = $"monthlyreport:{new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(-1):yyyy-MM}";

        var before = DateTime.Now;
        await _service.RunMonthlyReportAsync(db, async () =>
        {
            // Concurrent replica wins the claim while this one is still gathering subscriptions.
            await _factory.WithDbAsync(async other =>
            {
                other.SentReminders.Add(new SentReminderEntity { Key = key, SentAt = DateTime.Now });
                await other.SaveChangesAsync();
            });
            return await db.PushSubscriptions.ToListAsync();
        });
        var after = DateTime.Now;

        var sentRows = await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith("monthlyreport:")).ToListAsync();
        var subRows = await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == gone.Url).ToListAsync();
        if (GateOpen(before) && GateOpen(after))
        {
            // Only the winner's (injected) row exists, no duplicate from the loser - and the
            // loser never reached the send loop, so the 410 subscription was never removed.
            Assert.Single(sentRows);
            Assert.Single(subRows);
        }
        else if (!GateOpen(before) && !GateOpen(after))
        {
            Assert.Empty(sentRows);
            Assert.Single(subRows);
        }
        else
        {
            Assert.True(sentRows.Count <= 1, "never more than one claim row for the same month");
        }
    }
}
