using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>Which slot of a tick a subtask occupies.</summary>
internal enum WorkerSubtaskScope
{
    /// <summary>Runs once per AuthUser, inside that user's ambient context and the one
    /// StudyLifeDb/IServiceScope the whole user iteration shares.</summary>
    PerUser,

    /// <summary>Runs once per tick, outside the user loop - instance-wide work (VACUUM, backup
    /// dump, key-outbox drain) that must not be repeated per user.</summary>
    OncePerTick,
}

/// <summary>
/// One entry of <see cref="BackgroundTaskService"/>'s tick table: what the subtask is called, how
/// often it may fire, which slot it runs in, what to log when it throws, and the delegate itself.
/// The next-due bookkeeping that used to live in a _nextXxxRun field per subtask lives here now,
/// which is what lets the dispatch be a single loop instead of eighteen near-identical
/// if/try/catch/finally blocks.
/// </summary>
internal sealed class WorkerSubtask
{
    /// <summary>Stable identifier for the docs and tests (docs/ARCHITECTURE.md "Background
    /// services" documents the same list) - not used in any log message or metric.</summary>
    public required string Name { get; init; }

    public required WorkerSubtaskScope Scope { get; init; }

    /// <summary>How long after a run this subtask is due again. Null means "no gate": it runs on
    /// every tick the loop takes (Live Activity push, the calendar-gated reports, the key
    /// outbox - the latter two carry their own, different kind of gate inside).</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>Logged together with the exception when this subtask throws. One message per
    /// subtask, unchanged from when each block spelled it out itself.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Set for <see cref="WorkerSubtaskScope.PerUser"/>: gets the tick's shared
    /// DbContext plus the memoized push-subscription loader.</summary>
    public Func<StudyLifeDb, Func<Task<List<PushSubscriptionEntity>>>, Task>? RunForUser { get; init; }

    /// <summary>Set for <see cref="WorkerSubtaskScope.OncePerTick"/>: creates whatever scope it
    /// needs itself, inside the caller's try block.</summary>
    public Func<Task>? RunOnce { get; init; }

    // Starts at DateTime.MinValue like the former _nextXxxRun fields, so the first tick of a
    // process runs every gated subtask immediately - and a restart consequently re-runs even the
    // weekly ones, the deliberate trade-off documented on the weekly entries below.
    private DateTime _nextRun = DateTime.MinValue;

    /// <summary>Whether this subtask fires in the tick that is currently running.</summary>
    public bool DueThisTick { get; private set; }

    /// <summary>Decided once per tick, BEFORE the user loop: every user of one tick must see the
    /// same answer, exactly as when ExecuteAsync computed one "run..." bool per subtask up
    /// front.</summary>
    public void OpenTick(DateTime now) => DueThisTick = Interval is null || now >= _nextRun;

    /// <summary>Moves the gate forward. Called in a finally, so a throwing subtask does not retry
    /// every 5s for the rest of the process - unchanged from the former finally blocks. With
    /// several users this runs once per user with the same "now", which is idempotent.</summary>
    public void MarkRan(DateTime now)
    {
        if (Interval is { } interval) _nextRun = now + interval;
    }
}

public partial class BackgroundTaskService
{
    /// <summary>
    /// The tick table, in the exact order the subtasks ran before: the per-user ones first (in the
    /// order ExecuteAsync invoked them), then the instance-wide ones. docs/ARCHITECTURE.md
    /// documents the same list and WorkerSubtaskTableTests pins it.
    /// </summary>
    private WorkerSubtask[] BuildSubtasks() =>
    [
        PerUser("PushNotifications", PushNotificationCheckInterval,
            "Error in PushBackgroundService", RunPushNotificationsAsync),
        PerUser("LiveActivityPush", null,
            "Error in LiveActivityPushService", (db, _) => RunLiveActivityPushAsync(db)),
        PerUser("CaptureEnrichment", CaptureEnrichmentCheckInterval,
            "Error in CaptureEnrichmentService", (db, _) => RunCaptureEnrichmentAsync(db)),
        PerUser("CourseGoalReminder", CourseGoalReminderInterval,
            "Error in CourseGoalReminderService", RunCourseGoalReminderCheckAsync),
        PerUser("InactivityReminder", InactivityReminderInterval,
            "Error in InactivityReminderService", RunInactivityReminderCheckAsync),
        PerUser("PerCourseInactivityReminder", PerCourseInactivityReminderInterval,
            "Error in PerCourseInactivityReminderService", RunPerCourseInactivityCheckAsync),
        PerUser("StreakRiskReminder", StreakRiskReminderInterval,
            "Error in StreakRiskReminderService", RunStreakRiskCheckAsync),
        PerUser("WeeklyGoalNudge", WeeklyGoalNudgeInterval,
            "Error in WeeklyGoalNudgeService", RunWeeklyGoalNudgeCheckAsync),
        PerUser("CourseAlmostDoneReminder", CourseAlmostDoneReminderInterval,
            "Error in CourseAlmostDoneReminderService", RunCourseAlmostDoneCheckAsync),
        PerUser("BestStudyTimeReminder", BestStudyTimeReminderInterval,
            "Error in BestStudyTimeReminderService", RunBestStudyTimeCheckAsync),
        PerUser("ComebackNudge", ComebackNudgeInterval,
            "Error in ComebackNudgeService", RunComebackNudgeCheckAsync),
        PerUser("AchievementCheck", AchievementCheckInterval,
            "Error in AchievementCheckService", RunAchievementCheckAsync),
        // The three calendar-gated reports deliberately have no interval gate: their SentReminder
        // key is the real gate (it survives restarts), and the in-memory memo dictionaries above
        // only save the repeated DB lookup after sending.
        PerUser("WeeklyReport", null,
            "Error in WeeklyReportService", RunWeeklyReportAsync),
        PerUser("MonthlyReport", null,
            "Error in MonthlyReportService", RunMonthlyReportAsync),
        PerUser("DailyMotivation", null,
            "Error in DailyMotivationService", RunDailyMotivationAsync),

        // User-independent maintenance deliberately outside the user loop: VACUUM, backup dump and
        // key rotation affect the whole DB/instance and run exactly once per tick, regardless of
        // how many users exist.
        OncePerTick("AiKeyOutbox", null,
            "Error draining the AI key outbox", () => WithScopedDbAsync(RunAiKeyOutboxAsync)),
        // Weekly, and the gate resets on process restart like every other one - for a maintenance
        // task that means "at most weekly, more often with frequent deploys", which is harmless.
        OncePerTick("DatabaseMaintenance", DatabaseMaintenanceInterval,
            "Error during SQLite maintenance", () => WithScopedDbAsync(RunDatabaseMaintenanceAsync)),
        // Same restart behaviour as the maintenance task - harmless for a purely supplementary
        // safety dump (the last 4 weeks are retained regardless, see DatabaseBackupService).
        OncePerTick("BackupDump", BackupDumpInterval,
            "Error during the weekly database backup", RunBackupDumpAsync),
    ];

    /// <summary>The tick table of this instance. internal so WorkerSubtaskTableTests can pin the
    /// documented list (docs/ARCHITECTURE.md) against what actually gets dispatched.</summary>
    internal IReadOnlyList<WorkerSubtask> Subtasks => _subtasks;

    private static WorkerSubtask PerUser(string name, TimeSpan? interval, string errorMessage,
        Func<StudyLifeDb, Func<Task<List<PushSubscriptionEntity>>>, Task> run) =>
        new()
        {
            Name = name,
            Scope = WorkerSubtaskScope.PerUser,
            Interval = interval,
            ErrorMessage = errorMessage,
            RunForUser = run,
        };

    private static WorkerSubtask OncePerTick(string name, TimeSpan? interval, string errorMessage,
        Func<Task> run) =>
        new()
        {
            Name = name,
            Scope = WorkerSubtaskScope.OncePerTick,
            Interval = interval,
            ErrorMessage = errorMessage,
            RunOnce = run,
        };

    /// <summary>Scope + DbContext for an instance-wide subtask. Invoked from inside the dispatch's
    /// try block, so a broken scope lands in that subtask's catch like any other failure.</summary>
    private async Task WithScopedDbAsync(Func<StudyLifeDb, Task> body)
    {
        using var scope = _services.CreateScope();
        await body(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }

    /// <summary>
    /// The AuthUserIds this process handles in this tick. Null means a cancellation surfaced and
    /// the loop must end; an empty list means "nothing to do this tick, try again next time" -
    /// either because the user-list load failed (logged, never fatal) or because no shard was free.
    /// </summary>
    private async Task<List<int>?> LoadShardedAuthUserIdsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var userListScope = _services.CreateScope();
            var userListDb = userListScope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            var authUserIds = await userListDb.AuthUsers.AsNoTracking().Select(u => u.Id).ToListAsync(stoppingToken);
            // Partitioning across multiple worker replicas (see the _shardClaim field comment) -
            // with exactly 1 replica (default, StaticWorkerShardClaim) this filter is a no-op
            // (shard always 0, "id % 1 == 0" always true).
            var shard = await _shardClaim.ClaimOrRenewAsync(stoppingToken);
            return shard is int ordinal
                ? authUserIds.Where(id => id % _shardClaim.LastReplicaCount == ordinal).ToList()
                : new List<int>(); // no shard free - this tick processes no one, next tick retries
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Don't let the whole background loop die - the next tick will try again.
            _logger.LogError(ex, "Error loading the AuthUser list");
            return new List<int>();
        }
    }

    /// <summary>
    /// One user's slice of a tick: the ambient user context (which is what makes StudyLifeDb's
    /// global query filters user-specific), one scope and one DbContext shared by every subtask of
    /// this user, and a push-subscription fetch memoized across them.
    /// internal, so the dispatch itself can be tested against a hand-built table.
    /// </summary>
    internal async Task RunPerUserSubtasksAsync(IReadOnlyList<WorkerSubtask> subtasks, int authUserId, DateTime now)
    {
        using var userContext = CurrentUserAccessor.BeginBackgroundScope(authUserId);
        _currentAuthUserId = authUserId;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        // Subscriptions are identical for all push sub-tasks of this tick -
        // load once and share via the same DbContext instead of fetching repeatedly.
        List<PushSubscriptionEntity>? subscriptions = null;
        Task<List<PushSubscriptionEntity>> GetSubscriptionsAsync()
            => subscriptions != null
                ? Task.FromResult(subscriptions)
                : LoadSubscriptionsAsync();
        async Task<List<PushSubscriptionEntity>> LoadSubscriptionsAsync()
        {
            subscriptions = await db.PushSubscriptions.ToListAsync();
            return subscriptions;
        }

        foreach (var subtask in subtasks)
        {
            if (subtask.Scope != WorkerSubtaskScope.PerUser || !subtask.DueThisTick) continue;
            try
            {
                await subtask.RunForUser!(db, GetSubscriptionsAsync);
            }
            catch (Exception ex)
            {
                // One try/catch per subtask, as before: a failing subtask must not cost the
                // remaining ones of this tick their turn.
                _logger.LogError(ex, subtask.ErrorMessage);
            }
            finally
            {
                subtask.MarkRan(now);
            }
        }
    }

    /// <summary>Instance-wide subtasks, run once after the user loop. internal for the same reason
    /// as <see cref="RunPerUserSubtasksAsync"/>.</summary>
    internal async Task RunOncePerTickSubtasksAsync(IReadOnlyList<WorkerSubtask> subtasks, DateTime now)
    {
        foreach (var subtask in subtasks)
        {
            if (subtask.Scope != WorkerSubtaskScope.OncePerTick || !subtask.DueThisTick) continue;
            try
            {
                await subtask.RunOnce!();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, subtask.ErrorMessage);
            }
            finally
            {
                subtask.MarkRan(now);
            }
        }
    }
}
