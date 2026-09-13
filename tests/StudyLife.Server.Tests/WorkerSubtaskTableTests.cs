using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// The tick table replaced eighteen hand-written if/try/catch/finally blocks in ExecuteAsync, so
/// the table itself is now the thing that has to stay correct: a subtask dropped from it, or given
/// the wrong cadence, would silently stop firing (or start hammering) with nothing else to notice.
/// docs/ARCHITECTURE.md "Background services" documents exactly this list - these tests are its
/// executable counterpart.
/// </summary>
public class WorkerSubtaskTableTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WorkerSubtaskTableTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient(); // host (incl. migration + VAPID keys) must be up before construction
    }

    private static readonly TimeSpan Seconds30 = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);
    private static readonly TimeSpan Weekly = TimeSpan.FromDays(7);

    /// <summary>Name, slot and cadence of every subtask, in dispatch order. Null interval = runs
    /// on every 5s tick (no next-due gate of its own). The slot travels as a string because
    /// WorkerSubtaskScope is internal to the server assembly and xUnit needs public theory
    /// parameters - widening the enum just for the test would be the wrong trade.</summary>
    public static TheoryData<int, string, string, TimeSpan?> ExpectedTable() => new()
    {
        { 0, "PushNotifications", nameof(WorkerSubtaskScope.PerUser), Seconds30 },
        { 1, "LiveActivityPush", nameof(WorkerSubtaskScope.PerUser), null },
        { 2, "CaptureEnrichment", nameof(WorkerSubtaskScope.PerUser), Seconds30 },
        { 3, "CourseGoalReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 4, "InactivityReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 5, "PerCourseInactivityReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 6, "StreakRiskReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 7, "WeeklyGoalNudge", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 8, "CourseAlmostDoneReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 9, "BestStudyTimeReminder", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 10, "ComebackNudge", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 11, "AchievementCheck", nameof(WorkerSubtaskScope.PerUser), Hourly },
        { 12, "WeeklyReport", nameof(WorkerSubtaskScope.PerUser), null },
        { 13, "MonthlyReport", nameof(WorkerSubtaskScope.PerUser), null },
        { 14, "DailyMotivation", nameof(WorkerSubtaskScope.PerUser), null },
        { 15, "AiKeyOutbox", nameof(WorkerSubtaskScope.OncePerTick), null },
        { 16, "DatabaseMaintenance", nameof(WorkerSubtaskScope.OncePerTick), Weekly },
        { 17, "BackupDump", nameof(WorkerSubtaskScope.OncePerTick), Weekly },
    };

    [Theory]
    [MemberData(nameof(ExpectedTable))]
    public void Table_ContainsEverySubtask_InOrder_WithItsDocumentedCadence(
        int position, string name, string scope, TimeSpan? interval)
    {
        var subtask = BackgroundTaskServiceTestFactory.Create(_factory).Subtasks[position];

        Assert.Equal(name, subtask.Name);
        Assert.Equal(scope, subtask.Scope.ToString());
        Assert.Equal(interval, subtask.Interval);
    }

    /// <summary>Guards against an entry being added without updating the table above - the theory
    /// alone would happily ignore a nineteenth row.</summary>
    [Fact]
    public void Table_HasExactlyTheDocumentedNumberOfSubtasks()
    {
        Assert.Equal(ExpectedTable().Count(), BackgroundTaskServiceTestFactory.Create(_factory).Subtasks.Count);
    }

    /// <summary>Every entry must carry the delegate its slot dispatches - a per-user entry with
    /// only a RunOnce (or vice versa) would throw on its first tick.</summary>
    [Fact]
    public void EverySubtask_CarriesTheDelegateItsSlotDispatches_AndItsOwnErrorMessage()
    {
        var subtasks = BackgroundTaskServiceTestFactory.Create(_factory).Subtasks;

        foreach (var subtask in subtasks)
        {
            Assert.False(string.IsNullOrWhiteSpace(subtask.ErrorMessage));
            if (subtask.Scope == WorkerSubtaskScope.PerUser)
            {
                Assert.NotNull(subtask.RunForUser);
                Assert.Null(subtask.RunOnce);
            }
            else
            {
                Assert.NotNull(subtask.RunOnce);
                Assert.Null(subtask.RunForUser);
            }
        }

        Assert.Equal(subtasks.Count, subtasks.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(subtasks.Count, subtasks.Select(s => s.ErrorMessage).Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>
/// The dispatch loop's two invariants, driven against a hand-built table (the runners are internal
/// for exactly this): a subtask that throws is caught individually, and its next-due gate is still
/// moved forward - the "don't let the whole background loop die" promise the eighteen former
/// try/catch/finally blocks each spelled out for themselves.
/// </summary>
public class WorkerSubtaskDispatchTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public WorkerSubtaskDispatchTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
    }

    private static WorkerSubtask PerUser(string name, Action onRun, TimeSpan? interval = null) => new()
    {
        Name = name,
        Scope = WorkerSubtaskScope.PerUser,
        Interval = interval,
        ErrorMessage = $"Error in {name}",
        RunForUser = (_, _) =>
        {
            onRun();
            return Task.CompletedTask;
        },
    };

    private static WorkerSubtask OncePerTick(string name, Action onRun, TimeSpan? interval = null) => new()
    {
        Name = name,
        Scope = WorkerSubtaskScope.OncePerTick,
        Interval = interval,
        ErrorMessage = $"Error in {name}",
        RunOnce = () =>
        {
            onRun();
            return Task.CompletedTask;
        },
    };

    [Fact]
    public async Task PerUser_AThrowingSubtask_DoesNotStopTheOthersOfTheSameTick()
    {
        var ran = new List<string>();
        WorkerSubtask[] table =
        [
            PerUser("first", () => ran.Add("first")),
            PerUser("throws", () => throw new InvalidOperationException("subtask blew up")),
            PerUser("second", () => ran.Add("second")),
        ];
        var now = DateTime.UtcNow;
        foreach (var subtask in table) subtask.OpenTick(now);

        await BackgroundTaskServiceTestFactory.Create(_factory).RunPerUserSubtasksAsync(table, authUserId: 1, now);

        Assert.Equal(["first", "second"], ran);
    }

    [Fact]
    public async Task OncePerTick_AThrowingSubtask_DoesNotStopTheOthersOfTheSameTick()
    {
        var ran = new List<string>();
        WorkerSubtask[] table =
        [
            OncePerTick("throws", () => throw new InvalidOperationException("subtask blew up")),
            OncePerTick("after", () => ran.Add("after")),
        ];
        var now = DateTime.UtcNow;
        foreach (var subtask in table) subtask.OpenTick(now);

        await BackgroundTaskServiceTestFactory.Create(_factory).RunOncePerTickSubtasksAsync(table, now);

        Assert.Equal(["after"], ran);
    }

    /// <summary>The gate moves forward in a finally, so a permanently failing subtask backs off to
    /// its own interval instead of retrying every 5s for the rest of the process.</summary>
    [Fact]
    public async Task AThrowingSubtask_StillAdvancesItsNextDueGate()
    {
        var attempts = 0;
        var subtask = OncePerTick("throws", () =>
        {
            attempts++;
            throw new InvalidOperationException("subtask blew up");
        }, TimeSpan.FromHours(1));
        var service = BackgroundTaskServiceTestFactory.Create(_factory);

        var firstTick = DateTime.UtcNow;
        subtask.OpenTick(firstTick);
        Assert.True(subtask.DueThisTick);
        await service.RunOncePerTickSubtasksAsync([subtask], firstTick);

        // Next 5s tick: still inside the hour, so it must not run again.
        subtask.OpenTick(firstTick.AddSeconds(5));
        Assert.False(subtask.DueThisTick);
        await service.RunOncePerTickSubtasksAsync([subtask], firstTick.AddSeconds(5));

        // An hour later it is due again.
        subtask.OpenTick(firstTick.AddHours(1));
        Assert.True(subtask.DueThisTick);
        await service.RunOncePerTickSubtasksAsync([subtask], firstTick.AddHours(1));

        Assert.Equal(2, attempts);
    }

    /// <summary>An entry without an interval carries no gate at all and fires on every tick -
    /// Live Activity push depends on that.</summary>
    [Fact]
    public void AnUngatedSubtask_IsDueOnEveryTick()
    {
        var subtask = PerUser("ungated", () => { });
        var now = DateTime.UtcNow;

        subtask.OpenTick(now);
        Assert.True(subtask.DueThisTick);
        subtask.MarkRan(now);

        subtask.OpenTick(now.AddSeconds(5));
        Assert.True(subtask.DueThisTick);
    }

    /// <summary>The per-user runner loads the push subscriptions at most once per user and hands
    /// the same list to every subtask of that user - the memoization ExecuteAsync used to set up
    /// inline, and the reason the push subtasks do not each query the table themselves.</summary>
    [Fact]
    public async Task PerUser_SubscriptionLoader_IsSharedAcrossTheSubtasksOfOneUser()
    {
        List<PushSubscriptionEntity>? first = null;
        List<PushSubscriptionEntity>? second = null;
        WorkerSubtask[] table =
        [
            new()
            {
                Name = "a", Scope = WorkerSubtaskScope.PerUser, ErrorMessage = "Error in a",
                RunForUser = async (_, getSubscriptions) => first = await getSubscriptions(),
            },
            new()
            {
                Name = "b", Scope = WorkerSubtaskScope.PerUser, ErrorMessage = "Error in b",
                RunForUser = async (_, getSubscriptions) => second = await getSubscriptions(),
            },
        ];
        var now = DateTime.UtcNow;
        foreach (var subtask in table) subtask.OpenTick(now);

        await BackgroundTaskServiceTestFactory.Create(_factory).RunPerUserSubtasksAsync(table, authUserId: 1, now);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }
}
