using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;
using WebPush;

namespace StudyLife.Server.Services;

/// <summary>
/// Everything /api/sessions does once the HTTP layer is stripped away: loading the calendar and
/// history projections, the create/update/delete write paths with their CourseId resolution,
/// history-cache invalidation, the instant new-record push and the session webhook events.
/// <see cref="SessionsController"/> keeps only what is genuinely HTTP - route/verb binding,
/// the ETag/Cache-Control wrapper around the two cached GETs (see <see cref="CacheHelper"/>,
/// which needs the request/response), the uploaded-file handling of import-ics, and the mapping
/// of a <see cref="ServiceResult{T}"/> onto a status code.
/// </summary>
public interface ISessionService
{
    /// <summary>Cache key for the unbounded calendar list - already changes on every write via the
    /// per-user version counter, so the controller's TTL is only a memory bound (see
    /// <see cref="LoadAllAsync"/>'s caller).</summary>
    Task<string> AllCacheKeyAsync();

    /// <summary>Materializes the full, unbounded session list (the cache factory behind GET /api/sessions).</summary>
    Task<List<StudySessionDto>> LoadAllAsync();

    /// <summary>Cache key for <see cref="LoadHistoryAsync"/> - the parameters are part of it, so
    /// two different windows never share an entry.</summary>
    Task<string> HistoryCacheKeyAsync(int days, bool onlyCompleted);

    /// <summary>Materializes the long-term history window (the cache factory behind GET /api/sessions/history).</summary>
    Task<List<StudySessionDto>> LoadHistoryAsync(int days, bool onlyCompleted);

    /// <summary>Renders the subscribable iCalendar feed (RFC 5545) for its own, narrower window.</summary>
    Task<string> BuildIcsAsync();

    Task<ServiceResult<StudySessionDto>> CreateAsync(StudySessionDto dto);
    Task<ServiceResult<StudySessionDto>> UpdateAsync(int id, StudySessionDto dto);
    Task<ServiceResult> DeleteAsync(int id);
    Task DeleteSeriesAsync(string groupId, DateTime? fromDate);
}

public class SessionService : ISessionService
{
    /// <summary>Memory bound for the version-keyed GET caches, not a freshness mechanism - the key
    /// already changes on every write (per-user version counter), so the TTL never decides
    /// freshness. It used to be 15s - shorter than the 30s client poll, so the entry had always
    /// expired before the next poll and every poll paid the full query + serialization + cache
    /// write (2026-09 audit). Ten minutes lets polls and multiple open clients actually share the
    /// entry; stale versions age out by themselves.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Widest history window any client asks for (Stats/Wrapped achievements) - see
    /// <see cref="ClampHistoryDays"/>.</summary>
    private const int MaxHistoryDays = 3650;

    private static readonly Func<StudyLifeDb, IAsyncEnumerable<StudySessionDto>> _compiledGetAll =
        EF.CompileAsyncQuery((StudyLifeDb db) =>
            db.Sessions.AsNoTracking().Select(s => ToDto(s)));

    private static readonly Func<StudyLifeDb, DateTime, bool, DateTime, IAsyncEnumerable<StudySessionDto>> _compiledGetHistory =
        EF.CompileAsyncQuery((StudyLifeDb db, DateTime from, bool onlyCompleted, DateTime now) =>
            db.Sessions.AsNoTracking().Where(s => s.StartTime >= from)
                .Where(s => !onlyCompleted || s.IsCompleted || s.EndTime <= now)
                .Select(s => ToDto(s)));

    private readonly StudyLifeDb _db;
    private readonly SessionHistoryCacheVersion _historyCacheVersion;
    private readonly VapidKeys _vapidKeys;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ICourseResolver _courseResolver;
    private readonly ApnsSender _apnsSender;
    private readonly WebhooksProxyClient _webhooks;

    public SessionService(StudyLifeDb db, SessionHistoryCacheVersion historyCacheVersion, VapidKeysHolder vapidKeysHolder,
        ICurrentUserAccessor currentUser, ApnsSender apnsSender, ICourseResolver courseResolver, WebhooksProxyClient webhooks)
    {
        _db = db;
        _historyCacheVersion = historyCacheVersion;
        _vapidKeys = vapidKeysHolder.Keys!; // always set - see VapidKeysHolder comment
        _currentUser = currentUser;
        _apnsSender = apnsSender;
        _courseResolver = courseResolver;
        _webhooks = webhooks;
    }

    /// <summary>
    /// Clamps a caller-supplied history window instead of trusting it: Math.Abs(int.MinValue)
    /// throws (a 500 for ?days=-2147483648), and an arbitrarily large window is a full-history
    /// scan per request. Static so the controller can clamp BEFORE building the cache key, which
    /// must be derived from the effective window, not the raw query string.
    /// </summary>
    public static int ClampHistoryDays(int days) =>
        Math.Clamp(days == int.MinValue ? MaxHistoryDays : Math.Abs(days), 1, MaxHistoryDays);

    public async Task<string> AllCacheKeyAsync() =>
        $"sessions:all:{_currentUser.AuthUserId}:{await _historyCacheVersion.GetAsync(_currentUser.AuthUserId)}";

    public async Task<List<StudySessionDto>> LoadAllAsync()
    {
        // No date bounds - the client fetches this once and does all week/day navigation
        // itself (see AppStateService.cs), so a server-side window here just hides sessions
        // outside it. Was -7/+90 days originally; changed to unbounded so the calendar
        // shows the user's full session history, not just recent/near-future ones.
        var result = new List<StudySessionDto>();
        await foreach (var dto in _compiledGetAll(_db)) result.Add(dto);
        return result;
    }

    public async Task<string> HistoryCacheKeyAsync(int days, bool onlyCompleted) =>
        $"history:{_currentUser.AuthUserId}:{days}:{onlyCompleted}:{await _historyCacheVersion.GetAsync(_currentUser.AuthUserId)}";

    public async Task<List<StudySessionDto>> LoadHistoryAsync(int days, bool onlyCompleted)
    {
        // Audit finding Z1: StartTime/EndTime columns are naive local (see docs/ARCHITECTURE.md
        // "Single-Timezone Invariant"), so the window boundary compared against them must be
        // DateTime.Now too - it used to be DateTime.UtcNow here while the completed-cutoff
        // below already correctly used DateTime.Now, silently shifting the window's edge by
        // the container's UTC offset (e.g. a session from exactly "days" ago at 01:00 local
        // in UTC+2 could fall just outside a from-boundary computed in UTC).
        var from = DateTime.Now.AddDays(-Math.Abs(days));
        // "Completed" here means "counts as studied": either the Focus-Timer ran it to
        // completion, or its scheduled end has simply passed - not every study session
        // happens with the in-app timer running (e.g. reading offline), and those
        // shouldn't be invisible to streak/hours/balance-check just because nobody
        // clicked a button in the app.
        var result = new List<StudySessionDto>();
        await foreach (var dto in _compiledGetHistory(_db, from, onlyCompleted, DateTime.Now)) result.Add(dto);
        return result;
    }

    public async Task<string> BuildIcsAsync()
    {
        // Audit finding Z1: same fix as LoadHistoryAsync above - StartTime is naive local, so the
        // window boundaries compared against it must be DateTime.Now, not DateTime.UtcNow (which
        // would silently shift the window edge by the container's UTC offset). The DTSTAMP value
        // further below is a genuinely different case - RFC 5545 requires it in UTC - and is left
        // untouched.
        var from = DateTime.Now.AddDays(-7);
        var to = DateTime.Now.AddDays(90);
        var sessions = await _db.Sessions
            .Where(s => s.StartTime >= from && s.StartTime <= to)
            .OrderBy(s => s.StartTime)
            .ToListAsync();

        var sb = new System.Text.StringBuilder();
        void Line(string value) => sb.Append(value).Append("\r\n");

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//StudyLife//Sessions//DE");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:StudyLife");

        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        foreach (var s in sessions)
        {
            Line("BEGIN:VEVENT");
            Line($"UID:studylife-session-{s.Id}@studylife");
            Line($"DTSTAMP:{stamp}");
            Line($"DTSTART:{s.StartTime:yyyyMMddTHHmmss}");
            Line($"DTEND:{s.EndTime:yyyyMMddTHHmmss}");
            Line($"SUMMARY:{IcsEscape(s.CourseName)}");
            var description = string.IsNullOrWhiteSpace(s.Topic) ? s.Notes : s.Topic;
            if (!string.IsNullOrWhiteSpace(description)) Line($"DESCRIPTION:{IcsEscape(description!)}");
            Line($"STATUS:{(s.IsCompleted ? "CONFIRMED" : "TENTATIVE")}");
            Line("END:VEVENT");
        }

        Line("END:VCALENDAR");
        return sb.ToString();
    }

    public async Task<ServiceResult<StudySessionDto>> CreateAsync(StudySessionDto dto)
    {
        var error = Validate(dto);
        if (error != null) return ServiceResult<StudySessionDto>.Invalid(error);

        // Audit finding M2: CourseId must resolve against the user's full course universe
        // (built-in catalog + all their custom courses, see CourseResolver), and
        // CourseName/CourseColor are derived from it server-side - the client-supplied values
        // in dto are ignored from here on (still required to be non-empty above for backward
        // compatibility, but no longer trusted for their content).
        var course = await _courseResolver.ResolveAsync(dto.CourseId);
        if (course == null) return ServiceResult<StudySessionDto>.Invalid(CourseValidationMessages.UnknownCourseId(dto.CourseId));

        var entity = ToEntity(dto);
        entity.Id = 0;
        entity.CourseName = course.Name;
        entity.CourseColor = course.Color;
        _db.Sessions.Add(entity);
        await _db.SaveChangesAsync();
        await _historyCacheVersion.BumpAsync(_currentUser.AuthUserId);
        await CheckNewRecordAsync(entity);
        PublishSessionWebhookEvents(entity, isNewSession: true, wasCompletedBefore: false);
        return ServiceResult<StudySessionDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult<StudySessionDto>> UpdateAsync(int id, StudySessionDto dto)
    {
        var error = Validate(dto);
        if (error != null) return ServiceResult<StudySessionDto>.Invalid(error);

        var entity = await _db.Sessions.FindAsync(id);
        if (entity == null) return ServiceResult<StudySessionDto>.NotFound();

        // Audit finding M2, exemption: a CourseId UNCHANGED from the stored row is never
        // re-validated and its CourseName/CourseColor stay exactly as stamped at creation -
        // editing/completing a session of a since-deleted custom course (e.g. via the focus
        // timer) must keep working, and a later catalog rename must not silently rewrite
        // already-frozen rows. Only an ACTUALLY CHANGED CourseId goes through resolution again,
        // which re-derives (and re-freezes) CourseName/CourseColor from the newly bound course.
        if (dto.CourseId != entity.CourseId)
        {
            var course = await _courseResolver.ResolveAsync(dto.CourseId);
            if (course == null) return ServiceResult<StudySessionDto>.Invalid(CourseValidationMessages.UnknownCourseId(dto.CourseId));
            entity.CourseId = dto.CourseId;
            entity.CourseName = course.Name;
            entity.CourseColor = course.Color;
        }

        var oldStartTime = entity.StartTime;
        var wasCompletedBefore = entity.IsCompleted;
        Apply(dto, entity);

        // If the session start shifts, this invalidates the already-sent session reminders
        // (key "{id}:reminderN", see BackgroundTaskService.RunPushNotificationsAsync) -
        // without this reset, e.g. the 30-minute reminder for the old time would count as
        // "already sent" and never fire again relative to the new time. Other reminder types
        // (course goal, inactivity) are bound to CourseId/date instead of session id and are
        // therefore unaffected by a time shift.
        if (entity.StartTime != oldStartTime)
        {
            var keyPrefix = $"{id}:reminder";
            var staleReminders = await _db.SentReminders
                .Where(r => r.Key.StartsWith(keyPrefix))
                .ToListAsync();
            if (staleReminders.Count > 0)
                _db.SentReminders.RemoveRange(staleReminders);
        }

        await _db.SaveChangesAsync();
        await _historyCacheVersion.BumpAsync(_currentUser.AuthUserId);
        await CheckNewRecordAsync(entity);
        PublishSessionWebhookEvents(entity, isNewSession: false, wasCompletedBefore);
        return ServiceResult<StudySessionDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var entity = await _db.Sessions.FindAsync(id);
        if (entity == null) return ServiceResult.NotFound();
        _db.Sessions.Remove(entity);
        await _db.SaveChangesAsync();
        await _historyCacheVersion.BumpAsync(_currentUser.AuthUserId);
        _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.SessionDeleted,
            new { sessionId = entity.Id, courseName = entity.CourseName }, CancellationToken.None);
        return ServiceResult.Success();
    }

    public async Task DeleteSeriesAsync(string groupId, DateTime? fromDate)
    {
        var query = _db.Sessions.Where(s => s.RecurrenceGroupId == groupId);
        if (fromDate.HasValue) query = query.Where(s => s.StartTime.Date >= fromDate.Value.Date);
        _db.Sessions.RemoveRange(await query.ToListAsync());
        await _db.SaveChangesAsync();
        await _historyCacheVersion.BumpAsync(_currentUser.AuthUserId);
    }

    /// <summary>Fire-and-forget with CancellationToken.None (see TimerStateService.SaveAsync's
    /// identical reasoning - HttpContext.RequestAborted is not safe for work meant to outlive the
    /// request). isNewSession is an explicit flag, not inferred from wasCompletedBefore: an
    /// update of a still-incomplete session also has wasCompletedBefore=false, and must NOT
    /// re-fire session.created on every such edit - only <see cref="CreateAsync"/> ever passes
    /// isNewSession: true. session.completed fires exactly on the false-&gt;true transition,
    /// whether that happens at creation (a session logged as already complete) or later via an
    /// update - never re-fires on a subsequent edit of an already-completed session.</summary>
    private void PublishSessionWebhookEvents(StudySessionEntity entity, bool isNewSession, bool wasCompletedBefore)
    {
        var userId = _currentUser.AuthUserId;
        var payload = new
        {
            sessionId = entity.Id,
            courseId = entity.CourseId,
            courseName = entity.CourseName,
            durationMinutes = (entity.EndTime - entity.StartTime).TotalMinutes,
        };
        if (isNewSession)
        {
            _ = _webhooks.PublishEventAsync(userId, WebhookEventTypes.SessionCreated, payload, CancellationToken.None);
        }
        if (entity.IsCompleted && !wasCompletedBefore)
        {
            _ = _webhooks.PublishEventAsync(userId, WebhookEventTypes.SessionCompleted, payload, CancellationToken.None);
        }
    }

    /// <summary>
    /// Instant feedback on a new personal record: "longest single session so far" was chosen
    /// over "most hours on a calendar day", because it gets by without date grouping, using a
    /// single Max() comparison over completed sessions - simpler to compute and just as
    /// immediately understandable for the user ("this one session was your longest"). Runs
    /// directly in the write path (<see cref="CreateAsync"/>/<see cref="UpdateAsync"/>), NOT via
    /// the BackgroundTaskService polling cycle, so the feedback arrives immediately after
    /// finishing/saving.
    /// </summary>
    private async Task CheckNewRecordAsync(StudySessionEntity entity)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings is not { NewRecordNotificationsEnabled: true }) return;

        // "Studied" = same semantics as StudyMetrics.IsStudied (timer finished OR scheduled
        // end already in the past) - a merely planned, not-yet-started session cannot set a
        // record.
        var now = DateTime.Now;
        if (!(entity.IsCompleted || entity.EndTime <= now)) return;

        // Dedup per session id: prevents a repeat push if the same session is later
        // edited/moved again (a record conceptually "happens" only once).
        var key = $"newrecord:{entity.Id}";
        if (await _db.SentReminders.AnyAsync(r => r.Key == key)) return;

        var duration = entity.EndTime - entity.StartTime;

        var others = await _db.Sessions
            .Where(s => s.Id != entity.Id && (s.IsCompleted || s.EndTime <= now))
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();
        // Without a baseline (the very first studied session), a "record" is trivial and
        // would just feel like unmotivated spam - only meaningful from the second studied
        // session onward.
        if (others.Count == 0) return;

        var previousMaxHours = others.Max(s => (s.EndTime - s.StartTime).TotalHours);
        if (duration.TotalHours <= previousMaxHours) return;

        // Claim BEFORE sending: the unique index on SentReminders (AuthUserId, Key) is the
        // arbiter between concurrent writes of the same session, so exactly one request sends
        // the push. This replaces a process-wide SemaphoreSlim that serialized every session
        // write on the pod and was held across the outbound push HTTP calls - one slow push
        // provider stalled all POST/PUT /api/sessions (2026-09 audit P5). The DB constraint
        // also works across pods, which the in-process lock never did.
        var claim = new SentReminderEntity { Key = key, SentAt = now };
        _db.SentReminders.Add(claim);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _db.Entry(claim).State = EntityState.Detached; // a concurrent request won the claim
            return;
        }

        await SendNewRecordPushAsync(duration);
        _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.NewRecordSet,
            new { sessionId = entity.Id, durationMinutes = duration.TotalMinutes, previousBestMinutes = previousMaxHours * 60 },
            CancellationToken.None);
        await _db.SaveChangesAsync(); // persists the removal of expired subscriptions collected by the push
    }

    // Small, locally kept push-sending path instead of reusing
    // BackgroundTaskService.SendPushAsync/GetPushClient: those helpers are private instance
    // methods of a different class built for the 30s polling cycle. For this single instant-
    // feedback case, a lean, self-contained variant is enough.
    private async Task SendNewRecordPushAsync(TimeSpan duration)
    {
        var subscriptions = await _db.PushSubscriptions.ToListAsync();
        if (subscriptions.Count == 0) return;

        var title = "Neuer Rekord! 🏆";
        var body = $"Neuer Rekord: {duration.TotalHours:0.#} Stunden am Stück!";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        var client = new WebPushClient();
        client.SetVapidDetails(_vapidKeys.Subject, _vapidKeys.PublicKey, _vapidKeys.PrivateKey);

        var expired = new List<PushSubscriptionEntity>();
        await Task.WhenAll(subscriptions.Select(async sub =>
        {
            // APNs branch like in BackgroundTaskService.SendPushAsync: same payload,
            // different envelope; silent no-op without a configured channel.
            if (sub.Channel == PushSubscriptionEntity.ChannelApns)
            {
                if (!_apnsSender.Enabled || sub.ApnsToken is not { Length: > 0 }) return;
                var outcome = await _apnsSender.SendPayloadAsync(sub.ApnsToken, payload);
                if (outcome == ApnsSendOutcome.ExpiredToken)
                    lock (expired) expired.Add(sub);
                return;
            }

            try
            {
                var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await client.SendNotificationAsync(pushSub, payload);
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                lock (expired) expired.Add(sub);
            }
            catch (Exception)
            {
                // Best effort, like BackgroundTaskService.SendPushAsync - a single failed
                // delivery must not abort the instant feedback for other devices.
            }
        }));

        if (expired.Count > 0)
            _db.PushSubscriptions.RemoveRange(expired);
    }

    private static string IcsEscape(string value) =>
        value.Replace("\r", "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    private static string? Validate(StudySessionDto dto)
    {
        if (dto.CourseId <= 0) return "CourseId must be greater than 0.";
        if (string.IsNullOrWhiteSpace(dto.CourseName)) return "CourseName must not be empty.";
        if (dto.EndTime <= dto.StartTime) return "EndTime must be after StartTime.";
        // Plausibility limit against faulty client timezone calculations or similar: a single
        // session over 24h is unrealistic and would otherwise, e.g., permanently stick
        // CheckNewRecordAsync with a "record" that can never be reached again.
        if (dto.EndTime - dto.StartTime > TimeSpan.FromHours(24)) return "A session cannot last longer than 24 hours.";
        return null;
    }

    // internal instead of private: reused by BackupController (JSON export), MetricsController
    // and SummaryInputLoader, so none of them has to duplicate the same mapping again.
    internal static StudySessionDto ToDto(StudySessionEntity e) => new()
    {
        Id = e.Id,
        CourseId = e.CourseId,
        CourseName = e.CourseName,
        CourseColor = e.CourseColor,
        StartTime = e.StartTime,
        EndTime = e.EndTime,
        Topic = e.Topic,
        Notes = e.Notes,
        IsCompleted = e.IsCompleted,
        TimerModeId = e.TimerModeId,
        RecurrenceGroupId = e.RecurrenceGroupId,
    };

    private static StudySessionEntity ToEntity(StudySessionDto d) => new()
    {
        Id = d.Id,
        CourseId = d.CourseId,
        CourseName = d.CourseName,
        CourseColor = d.CourseColor,
        StartTime = d.StartTime,
        EndTime = d.EndTime,
        Topic = d.Topic,
        Notes = d.Notes,
        IsCompleted = d.IsCompleted,
        TimerModeId = d.TimerModeId,
        RecurrenceGroupId = d.RecurrenceGroupId,
    };

    // Deliberately does NOT touch CourseId/CourseName/CourseColor: UpdateAsync above already
    // decided those explicitly (re-resolved on an actual CourseId change, left untouched -
    // frozen - otherwise, see the audit finding M2 comment there).
    private static void Apply(StudySessionDto d, StudySessionEntity e)
    {
        e.StartTime = d.StartTime;
        e.EndTime = d.EndTime;
        e.Topic = d.Topic;
        e.Notes = d.Notes;
        e.IsCompleted = d.IsCompleted;
        e.TimerModeId = d.TimerModeId;
        e.RecurrenceGroupId = d.RecurrenceGroupId;
    }
}
