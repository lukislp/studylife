using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using WebPush;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService : BackgroundService
{
    // Used to be 30s, until step D (Live Activity push) was added: phase transitions need to
    // arrive promptly (LiveActivityBridge.swift marks the card as "stale" after a 12s grace
    // period), all other checks remain unaffected by the more frequent outer loop thanks to
    // their own hourly gates - only the "is X due" comparisons now run more often, not the
    // checks themselves.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CourseGoalReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InactivityReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan PerCourseInactivityReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StreakRiskReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan WeeklyGoalNudgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan CourseAlmostDoneReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BestStudyTimeReminderInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ComebackNudgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan AchievementCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DatabaseMaintenanceInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan BackupDumpInterval = TimeSpan.FromDays(7);

    private readonly IServiceProvider _services;
    private readonly VapidKeys _vapidKeys;
    private readonly ILogger<BackgroundTaskService> _logger;
    // Null in Postgres mode (not registered there, see Program.cs) - the raw SQLite backup
    // is deliberately a single-instance feature (see RunBackupDumpAsync/RunDatabaseMaintenanceAsync).
    private readonly DatabaseBackupService? _backupService;
    private WebPushClient? _pushClient;

    private DateTime _nextCourseGoalReminderRun = DateTime.MinValue;
    private DateTime _nextInactivityReminderRun = DateTime.MinValue;
    private DateTime _nextPerCourseInactivityReminderRun = DateTime.MinValue;
    private DateTime _nextStreakRiskReminderRun = DateTime.MinValue;
    private DateTime _nextWeeklyGoalNudgeRun = DateTime.MinValue;
    private DateTime _nextCourseAlmostDoneReminderRun = DateTime.MinValue;
    private DateTime _nextBestStudyTimeReminderRun = DateTime.MinValue;
    private DateTime _nextComebackNudgeRun = DateTime.MinValue;
    private DateTime _nextAchievementCheckRun = DateTime.MinValue;
    // Resets like the other next-due timestamps on every process restart - for a maintenance
    // task that means "at most weekly, more often with frequent deploys", which is harmless and
    // deliberately keeps the same restart trade-off as the other gates.
    private DateTime _nextDatabaseMaintenanceRun = DateTime.MinValue;
    // Same restart behavior as _nextDatabaseMaintenanceRun: "at most weekly", correspondingly
    // more often with frequent deploys - harmless for a pure supplementary safety dump
    // (the last 4 weeks are retained regardless, see DatabaseBackupService).
    private DateTime _nextBackupDumpRun = DateTime.MinValue;
    // No _nextRun gate for the weekly report: the SentReminder key is the gate (survives
    // restarts). This memo only prevents the DB from being queried for the key every 30s on
    // Sunday evening after sending, until midnight. Kept per AuthUserId since the multi-user
    // rework (dictionary instead of a single field), so user A's send doesn't gate user B.
    private readonly Dictionary<int, string> _weeklyReportSentForWeek = new();
    // Same pattern for the daily motivation: the SentReminder key is the actual gate,
    // the memo only saves the 30s DB queries after sending, until midnight.
    private readonly Dictionary<int, string> _dailyMotivationSentForDay = new();
    // Same pattern for the monthly report (see _weeklyReportSentForWeek): the SentReminder key
    // is the actual gate, the memo only saves the 30s DB queries after sending, until the
    // next month change.
    private readonly Dictionary<int, string> _monthlyReportSentForMonth = new();

    // AuthUserId of the user iteration currently running in ExecuteAsync - only used for the
    // memo dictionaries above. On direct Run* calls from tests it stays 0, which consistently
    // addresses the same memo row there as before the multi-user rework.
    private int _currentAuthUserId;

    // User partitioning for multiple worker replicas: each replica now only sweeps AuthUserIds
    // with "id % ReplicaCount == current shard" instead of redundantly sweeping all users - real
    // work distribution instead of just redundant sweeping + claim-first dedup. The shard AND
    // the replica count it is based on are determined TOGETHER PER TICK via _shardClaim (see the
    // IWorkerShardClaim.LastReplicaCount comment: both values must come from the same claim call,
    // otherwise they could drift apart if the replica count changes mid-tick).
    // Default (StaticWorkerShardClaim): shard is always 0, ReplicaCount always 1, "id % 1 == 0"
    // is always true - identical behavior to single-instance/docker-compose operation without config.
    // Where the replica count comes from (a static config value or a live query of the
    // Kubernetes deployment resource for safe HPA autoscaling) is up to
    // IWorkerReplicaCountProvider, see its comment - transparent to this class.
    // TryClaimReminderAsync additionally remains in place as a safety net for the brief
    // transition during a replica count change, when partition boundaries briefly overlap.
    private readonly IWorkerShardClaim _shardClaim;

    private readonly ApnsSender _apnsSender;
    // Optional constructor param (like _backupService) purely so the 4 existing direct-
    // construction unit tests (none of which exercise capture enrichment) don't all need
    // updating for an unrelated new dependency - always resolved for real via DI in production
    // (registered as a singleton in Program.cs, unlike _backupService's genuinely conditional
    // registration). CaptureEnrichment.cs's Enabled gate covers both "not configured" and "not
    // passed in a test" identically.
    private readonly AiProxyClient? _aiProxyClient;

    // True only on a confirmed demo instance (DemoModeGuard) - gates the one worker job that
    // can write OUTWARD on behalf of visitor-created data (capture enrichment posting to
    // studylife-ai). Optional constructor param like _aiProxyClient, and for the same reason:
    // the direct-construction unit tests don't pass an IConfiguration, and "not passed" and
    // "not a demo" behave identically (false). In production DI always injects the host's
    // IConfiguration, so this agrees with Program.cs about whether demo mode is armed.
    private readonly bool _demoReadOnly;

    // Clock seam for the wall-clock gates in the sub-task partials (weekly report on Sunday
    // evenings, monthly report on the 1st, daily motivation from 8 AM, ...). Production always
    // uses TimeProvider.System - LocalNow below is then byte-identical to the previous direct
    // DateTime.Now calls. Only tests inject a fixed provider, so the gated bodies become
    // deterministically reachable instead of depending on when the suite happens to run.
    private readonly TimeProvider _time;

    /// <summary>Local wall-clock "now", same naive-local semantics as DateTime.Now
    /// (the whole app treats times as floating local time, see docs/ARCHITECTURE.md).</summary>
    private DateTime LocalNow => _time.GetLocalNow().DateTime;

    public BackgroundTaskService(
        IServiceProvider services,
        VapidKeysHolder vapidKeysHolder,
        ILogger<BackgroundTaskService> logger,
        ApnsSender apnsSender,
        IWorkerShardClaim? shardClaim = null,
        DatabaseBackupService? backupService = null,
        TimeProvider? timeProvider = null,
        AiProxyClient? aiProxyClient = null,
        IConfiguration? configuration = null)
    {
        _services = services;
        _vapidKeys = vapidKeysHolder.Keys!; // always set - see VapidKeysHolder comment
        _logger = logger;
        _apnsSender = apnsSender;
        _backupService = backupService;
        _shardClaim = shardClaim ?? new StaticWorkerShardClaim();
        _time = timeProvider ?? TimeProvider.System;
        _aiProxyClient = aiProxyClient;
        _demoReadOnly = configuration is not null && DemoModeGuard.IsEnabled(configuration);
    }

    private WebPushClient GetPushClient()
    {
        if (_pushClient != null) return _pushClient;
        _pushClient = new WebPushClient();
        _pushClient.SetVapidDetails(_vapidKeys.Subject, _vapidKeys.PublicKey, _vapidKeys.PrivateKey);
        return _pushClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var tickStarted = Stopwatch.GetTimestamp();
            var now = DateTime.UtcNow;
            var runCourseGoalReminder = now >= _nextCourseGoalReminderRun;
            var runInactivityReminder = now >= _nextInactivityReminderRun;
            var runPerCourseInactivityReminder = now >= _nextPerCourseInactivityReminderRun;
            var runStreakRiskReminder = now >= _nextStreakRiskReminderRun;
            var runWeeklyGoalNudge = now >= _nextWeeklyGoalNudgeRun;
            var runCourseAlmostDoneReminder = now >= _nextCourseAlmostDoneReminderRun;
            var runBestStudyTimeReminder = now >= _nextBestStudyTimeReminderRun;
            var runComebackNudge = now >= _nextComebackNudgeRun;
            var runAchievementCheck = now >= _nextAchievementCheckRun;
            var runDatabaseMaintenance = now >= _nextDatabaseMaintenanceRun;
            var runBackupDump = now >= _nextBackupDumpRun;

            // Outer user loop (multi-user foundation, phase 1): all user-related checks run
            // once PER AuthUserEntity, with context set via AsyncLocal
            // (CurrentUserAccessor.BeginBackgroundScope) - the global query filters in
            // StudyLifeDb thereby make every existing query automatically user-specific,
            // the check logic itself remains unchanged. Today exactly one user exists,
            // but the structure supports phase 2/3 (multiple users).
            List<int> authUserIds;
            try
            {
                using var userListScope = _services.CreateScope();
                var userListDb = userListScope.ServiceProvider.GetRequiredService<StudyLifeDb>();
                authUserIds = await userListDb.AuthUsers.AsNoTracking().Select(u => u.Id).ToListAsync(stoppingToken);
                // Partitioning across multiple worker replicas (see field comment above) -
                // with exactly 1 replica (default, StaticWorkerShardClaim) this filter is a
                // no-op (shard always 0, "id % 1 == 0" always true).
                var shard = await _shardClaim.ClaimOrRenewAsync(stoppingToken);
                authUserIds = shard is int ordinal
                    ? authUserIds.Where(id => id % _shardClaim.LastReplicaCount == ordinal).ToList()
                    : new List<int>(); // no shard free - this tick processes no one, next tick retries
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Don't let the whole background loop die - the next tick will try again.
                _logger.LogError(ex, "Error loading the AuthUser list");
                authUserIds = new List<int>();
            }

            foreach (var authUserId in authUserIds)
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

                try
                {
                    await RunPushNotificationsAsync(db, GetSubscriptionsAsync);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in PushBackgroundService");
                }

                try
                {
                    await RunLiveActivityPushAsync(db);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in LiveActivityPushService");
                }

                try
                {
                    await RunCaptureEnrichmentAsync(db);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in CaptureEnrichmentService");
                }

                if (runCourseGoalReminder)
                {
                    try
                    {
                        await RunCourseGoalReminderCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in CourseGoalReminderService");
                    }
                    finally
                    {
                        _nextCourseGoalReminderRun = now + CourseGoalReminderInterval;
                    }
                }

                if (runInactivityReminder)
                {
                    try
                    {
                        await RunInactivityReminderCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in InactivityReminderService");
                    }
                    finally
                    {
                        _nextInactivityReminderRun = now + InactivityReminderInterval;
                    }
                }

                if (runPerCourseInactivityReminder)
                {
                    try
                    {
                        await RunPerCourseInactivityCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in PerCourseInactivityReminderService");
                    }
                    finally
                    {
                        _nextPerCourseInactivityReminderRun = now + PerCourseInactivityReminderInterval;
                    }
                }

                if (runStreakRiskReminder)
                {
                    try
                    {
                        await RunStreakRiskCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in StreakRiskReminderService");
                    }
                    finally
                    {
                        _nextStreakRiskReminderRun = now + StreakRiskReminderInterval;
                    }
                }

                if (runWeeklyGoalNudge)
                {
                    try
                    {
                        await RunWeeklyGoalNudgeCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in WeeklyGoalNudgeService");
                    }
                    finally
                    {
                        _nextWeeklyGoalNudgeRun = now + WeeklyGoalNudgeInterval;
                    }
                }

                if (runCourseAlmostDoneReminder)
                {
                    try
                    {
                        await RunCourseAlmostDoneCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in CourseAlmostDoneReminderService");
                    }
                    finally
                    {
                        _nextCourseAlmostDoneReminderRun = now + CourseAlmostDoneReminderInterval;
                    }
                }

                if (runBestStudyTimeReminder)
                {
                    try
                    {
                        await RunBestStudyTimeCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in BestStudyTimeReminderService");
                    }
                    finally
                    {
                        _nextBestStudyTimeReminderRun = now + BestStudyTimeReminderInterval;
                    }
                }

                if (runComebackNudge)
                {
                    try
                    {
                        await RunComebackNudgeCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in ComebackNudgeService");
                    }
                    finally
                    {
                        _nextComebackNudgeRun = now + ComebackNudgeInterval;
                    }
                }

                if (runAchievementCheck)
                {
                    try
                    {
                        await RunAchievementCheckAsync(db, GetSubscriptionsAsync);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in AchievementCheckService");
                    }
                    finally
                    {
                        _nextAchievementCheckRun = now + AchievementCheckInterval;
                    }
                }

                try
                {
                    await RunWeeklyReportAsync(db, GetSubscriptionsAsync);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in WeeklyReportService");
                }

                try
                {
                    await RunMonthlyReportAsync(db, GetSubscriptionsAsync);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in MonthlyReportService");
                }

                try
                {
                    await RunDailyMotivationAsync(db, GetSubscriptionsAsync);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in DailyMotivationService");
                }

            }
            _currentAuthUserId = 0;

            // User-independent maintenance tasks deliberately run OUTSIDE the user loop:
            // VACUUM, backup dump, and key rotation affect the entire DB/instance and should
            // run exactly once per tick, regardless of how many users exist.

            try
            {
                using var scope = _services.CreateScope();
                await RunAiKeyOutboxAsync(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error draining the AI key outbox");
            }

            if (runDatabaseMaintenance)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    await RunDatabaseMaintenanceAsync(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SQLite maintenance");
                }
                finally
                {
                    _nextDatabaseMaintenanceRun = now + DatabaseMaintenanceInterval;
                }
            }

            if (runBackupDump)
            {
                try
                {
                    await RunBackupDumpAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during the weekly database backup");
                }
                finally
                {
                    _nextBackupDumpRun = now + BackupDumpInterval;
                }
            }

            StudyLifeMetrics.WorkerTickDuration.Record(Stopwatch.GetElapsedTime(tickStarted).TotalSeconds);
            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    // Claim-first instead of check-then-act: SentReminders has a unique index on
    // (AuthUserId, Key) - committing the claim BEFORE sending the push turns this insert into
    // the actual distributed lock. Two worker replicas running concurrently and claiming the
    // same key are thereby guaranteed not to both send the push: only whoever commits the
    // insert first sends - the loser gets a DbUpdateException and aborts BEFORE sending.
    internal async Task<bool> TryClaimReminderAsync(StudyLifeDb db, string key, DateTime sentAt)
    {
        var claim = new SentReminderEntity { Key = key, SentAt = sentAt };
        db.SentReminders.Add(claim);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Only remove the failed claim entry, not the entire change tracker -
            // otherwise already-staged, not-yet-saved changes (e.g. expired push subscriptions
            // from a previous loop iteration, see RunCourseAlmostDoneCheckAsync) would be
            // incorrectly discarded here instead of just being saved again later.
            db.Entry(claim).State = EntityState.Detached;
            return false;
        }
    }

    private readonly record struct PushSendResult(PushSubscriptionEntity Subscription, bool Expired);

    // Sends to a single subscription; error handling per task so that parallel sending
    // (Task.WhenAll) doesn't mutate shared state - aggregation happens afterward in the caller.
    private async Task<PushSendResult> SendPushAsync(WebPushClient client, PushSubscriptionEntity sub, string payload, string warningTemplate)
    {
        // APNs channel (native app shell): same payload, different envelope. Without a
        // configured ApnsSender (free tier/no p8 key) this is a silent no-op -
        // the subscription remains and becomes active as soon as the channel is configured.
        if (sub.Channel == PushSubscriptionEntity.ChannelApns)
        {
            if (!_apnsSender.Enabled || sub.ApnsToken is not { Length: > 0 })
                return new PushSendResult(sub, false);
            var outcome = await _apnsSender.SendPayloadAsync(sub.ApnsToken, payload);
            return new PushSendResult(sub, outcome == ApnsSendOutcome.ExpiredToken);
        }

        try
        {
            var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
            await client.SendNotificationAsync(pushSub, payload);
            return new PushSendResult(sub, false);
        }
        catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            return new PushSendResult(sub, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, warningTemplate, sub.Endpoint);
            return new PushSendResult(sub, false);
        }
    }
}
