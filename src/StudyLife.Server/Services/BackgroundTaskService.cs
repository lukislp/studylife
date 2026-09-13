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
    // Push notifications and capture enrichment used to run on every tick unconditionally -
    // harmless at the old 30s TickInterval, but became a needless 6x DB-query multiplier once
    // the tick was shortened to 5s for Live Activity (which is the only one of the three that
    // actually needs that granularity). Gated like the hourly reminders below, just with a much
    // shorter interval - "not up to an hour late" for captures, "session reminders stay minute-
    // accurate" for pushes, both comfortably satisfied by 30s.
    private static readonly TimeSpan PushNotificationCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CaptureEnrichmentCheckInterval = TimeSpan.FromSeconds(30);
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

    // What a tick actually dispatches: one descriptor per subtask (name, interval, per-user vs
    // once-per-tick, the delegate) instead of one "_next<Subtask>Run" field plus one
    // if/try/catch/finally block each. Built once per instance; the next-due state lives in the
    // descriptor (see WorkerSubtask in BackgroundTaskService.Tick.cs).
    private readonly WorkerSubtask[] _subtasks;

    // No interval gate for the weekly report: the SentReminder key is the gate (survives
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
        // Last: the table's delegates are method groups over this instance, so every field they
        // close over is already assigned.
        _subtasks = BuildSubtasks();
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
            // One next-due decision per subtask, taken up front so every user of this tick sees
            // the same answer - the same thing the former "run<Subtask>" bools did here.
            foreach (var subtask in _subtasks) subtask.OpenTick(now);

            // Outer user loop (multi-user foundation, phase 1): all user-related checks run
            // once PER AuthUserEntity, with context set via AsyncLocal
            // (CurrentUserAccessor.BeginBackgroundScope) - the global query filters in
            // StudyLifeDb thereby make every existing query automatically user-specific,
            // the check logic itself remains unchanged. Today exactly one user exists,
            // but the structure supports phase 2/3 (multiple users).
            var authUserIds = await LoadShardedAuthUserIdsAsync(stoppingToken);
            if (authUserIds is null) break; // cancelled while loading the user list / claiming

            foreach (var authUserId in authUserIds)
                await RunPerUserSubtasksAsync(_subtasks, authUserId, now);
            _currentAuthUserId = 0;

            await RunOncePerTickSubtasksAsync(_subtasks, now);

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
