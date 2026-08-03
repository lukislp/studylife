using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;
using WebPush;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly SessionHistoryCacheVersion _historyCacheVersion;
    private readonly VapidKeys _vapidKeys;
    private readonly ICurrentUserAccessor _currentUser;

    private static readonly Func<StudyLifeDb, DateTime, DateTime, IAsyncEnumerable<StudySessionDto>> _compiledGetAll =
        EF.CompileAsyncQuery((StudyLifeDb db, DateTime from, DateTime to) =>
            db.Sessions.AsNoTracking().Where(s => s.StartTime >= from && s.StartTime <= to).Select(s => ToDto(s)));

    private static readonly Func<StudyLifeDb, DateTime, bool, DateTime, IAsyncEnumerable<StudySessionDto>> _compiledGetHistory =
        EF.CompileAsyncQuery((StudyLifeDb db, DateTime from, bool onlyCompleted, DateTime now) =>
            db.Sessions.AsNoTracking().Where(s => s.StartTime >= from)
                .Where(s => !onlyCompleted || s.IsCompleted || s.EndTime <= now)
                .Select(s => ToDto(s)));

    private readonly ApnsSender _apnsSender;

    public SessionsController(StudyLifeDb db, IDistributedCache cache, SessionHistoryCacheVersion historyCacheVersion, VapidKeysHolder vapidKeysHolder,
        ICurrentUserAccessor currentUser, ApnsSender apnsSender)
    {
        _db = db;
        _cache = cache;
        _historyCacheVersion = historyCacheVersion;
        _vapidKeys = vapidKeysHolder.Keys!; // always set - see VapidKeysHolder comment
        _currentUser = currentUser;
        _apnsSender = apnsSender;
    }

    [HttpGet]
    public Task<ActionResult<IEnumerable<StudySessionDto>>> GetAll()
    {
        var cacheKey = $"sessions:all:{_currentUser.AuthUserId}:{_historyCacheVersion.Value}";
        // 15s TTL - half the 30s client poll interval, so near-simultaneous polls from
        // multiple open clients collapse onto one query while real changes still show
        // up within about one poll cycle.
        return _cache.GetOrSetAsync<IEnumerable<StudySessionDto>>(this, cacheKey, TimeSpan.FromSeconds(15), async () =>
        {
            var from = DateTime.UtcNow.AddDays(-7);
            var to = DateTime.UtcNow.AddDays(90);
            var result = new List<StudySessionDto>();
            await foreach (var dto in _compiledGetAll(_db, from, to)) result.Add(dto);
            return result;
        });
    }

    [HttpPost]
    public async Task<ActionResult<StudySessionDto>> Create(StudySessionDto dto)
    {
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        var entity = ToEntity(dto);
        entity.Id = 0;
        _db.Sessions.Add(entity);
        await _db.SaveChangesAsync();
        _historyCacheVersion.Value++;
        await CheckNewRecordAsync(entity);
        return ToDto(entity);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, StudySessionDto dto)
    {
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        var entity = await _db.Sessions.FindAsync(id);
        if (entity == null) return NotFound();
        var oldStartTime = entity.StartTime;
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
        _historyCacheVersion.Value++;
        await CheckNewRecordAsync(entity);
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.Sessions.FindAsync(id);
        if (entity == null) return NotFound();
        _db.Sessions.Remove(entity);
        await _db.SaveChangesAsync();
        _historyCacheVersion.Value++;
        return NoContent();
    }

    [HttpDelete("series/{groupId}")]
    public async Task<IActionResult> DeleteSeries(string groupId, [FromQuery] DateTime? fromDate)
    {
        var query = _db.Sessions.Where(s => s.RecurrenceGroupId == groupId);
        if (fromDate.HasValue) query = query.Where(s => s.StartTime.Date >= fromDate.Value.Date);
        _db.Sessions.RemoveRange(await query.ToListAsync());
        await _db.SaveChangesAsync();
        _historyCacheVersion.Value++;
        return NoContent();
    }

    /// <summary>
    /// Long-term history (default: 1 year, completed sessions only) for analytics charts
    /// and dashboard calculations that need to look back further than ±7/90 days (streak,
    /// monthly quota, weekly trend) - <see cref="GetAll"/> deliberately only returns this narrow
    /// window for calendar/dashboard "today/upcoming" displays. <paramref name="onlyCompleted"/>=false
    /// also returns non-completed sessions (for calculations that count "all sessions in the
    /// period" instead of only completed ones, e.g. weekly hours/trend, analogous to the
    /// "This week" tile).
    /// </summary>
    [HttpGet("history")]
    public Task<ActionResult<IEnumerable<StudySessionDto>>> GetHistory([FromQuery] int days = 365, [FromQuery] bool onlyCompleted = true)
    {
        var cacheKey = $"history:{_currentUser.AuthUserId}:{days}:{onlyCompleted}:{_historyCacheVersion.Value}";
        return _cache.GetOrSetAsync<IEnumerable<StudySessionDto>>(this, cacheKey, TimeSpan.FromSeconds(60), async () =>
        {
            var from = DateTime.UtcNow.AddDays(-Math.Abs(days));
            // "Completed" here means "counts as studied": either the Focus-Timer ran it to
            // completion, or its scheduled end has simply passed - not every study session
            // happens with the in-app timer running (e.g. reading offline), and those
            // shouldn't be invisible to streak/hours/balance-check just because nobody
            // clicked a button in the app.
            var result = new List<StudySessionDto>();
            await foreach (var dto in _compiledGetHistory(_db, from, onlyCompleted, DateTime.Now)) result.Add(dto);
            return result;
        });
    }

    /// <summary>
    /// Subscribable iCalendar feed of the same sessions as <see cref="GetAll"/>,
    /// for Google/Apple Calendar & co. Times are written as "floating" (no TZID,
    /// no Z suffix) because StartTime/EndTime are naive local time -
    /// see docs/ARCHITECTURE.md for the app's timezone handling.
    /// </summary>
    [HttpGet("ics")]
    public async Task<IActionResult> GetIcs()
    {
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow.AddDays(90);
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
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/calendar; charset=utf-8");
    }

    /// <summary>
    /// Accepts an uploaded .ics file and returns the parsed VEVENTs for review in the client -
    /// deliberately does NOT create any sessions yet, because the course (CourseId/name)
    /// cannot be inferred from a foreign .ics and the user has to choose it per appointment
    /// in the client (see Calendar.ImportIcs.razor.cs). The actual creation then happens via
    /// the normal POST /api/sessions, once per appointment confirmed by the user - no dedicated
    /// bulk-insert endpoint, to avoid maintaining duplicate validation/cache-invalidation logic
    /// here. See IcsImportParser for the scope (no RRULE expansion, best-effort TZID).
    /// </summary>
    [HttpPost("import-ics")]
    [RequestSizeLimit(10L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10L * 1024 * 1024)]
    public async Task<ActionResult<IcsImportResultDto>> ImportIcs(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Keine Datei hochgeladen." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8))
            content = await reader.ReadToEndAsync();

        var events = IcsImportParser.Parse(content);
        return Ok(new IcsImportResultDto { Events = events });
    }

    /// <summary>
    /// Instant feedback on a new personal record: "longest single session so far" was chosen
    /// over "most hours on a calendar day", because it gets by without date grouping, using a
    /// single Max() comparison over completed sessions - simpler to compute and just as
    /// immediately understandable for the user ("this one session was your longest"). Runs
    /// directly in the request handler (Create/Update), NOT via the BackgroundTaskService
    /// polling cycle, so the feedback arrives immediately after finishing/saving.
    /// </summary>
    // Serializes CheckNewRecordAsync process-wide: without this, two nearly simultaneous
    // create/update requests (two browser tabs, a double click) could both read the same old
    // record before the other session is committed, and both incorrectly trigger a "new
    // record" push. For this app's single-instance deployment (one Kestrel process on the
    // Pi) an in-process lock is sufficient; a DB transaction wouldn't be any more precise
    // with SQLite anyway.
    private static readonly SemaphoreSlim _newRecordLock = new(1, 1);

    private async Task CheckNewRecordAsync(StudySessionEntity entity)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings is not { NewRecordNotificationsEnabled: true }) return;

        // "Studied" = same semantics as StudyMetrics.IsStudied (timer finished OR scheduled
        // end already in the past) - a merely planned, not-yet-started session cannot set a
        // record.
        var now = DateTime.Now;
        if (!(entity.IsCompleted || entity.EndTime <= now)) return;

        await _newRecordLock.WaitAsync();
        try
        {
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

            await SendNewRecordPushAsync(duration);

            _db.SentReminders.Add(new SentReminderEntity { Key = key, SentAt = now });
            await _db.SaveChangesAsync();
        }
        finally
        {
            _newRecordLock.Release();
        }
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
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

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

    // internal instead of private: reused by BackupController (JSON export), so the export
    // projection doesn't have to duplicate the same mapping a second time.
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

    private static void Apply(StudySessionDto d, StudySessionEntity e)
    {
        e.CourseId = d.CourseId;
        e.CourseName = d.CourseName;
        e.CourseColor = d.CourseColor;
        e.StartTime = d.StartTime;
        e.EndTime = d.EndTime;
        e.Topic = d.Topic;
        e.Notes = d.Notes;
        e.IsCompleted = d.IsCompleted;
        e.TimerModeId = d.TimerModeId;
        e.RecurrenceGroupId = d.RecurrenceGroupId;
    }
}
