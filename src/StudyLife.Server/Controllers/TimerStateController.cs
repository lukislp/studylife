using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/timerstate")]
public class TimerStateController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly WebhooksProxyClient _webhooks;
    private readonly ICurrentUserAccessor _currentUser;

    public TimerStateController(StudyLifeDb db, WebhooksProxyClient webhooks, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _webhooks = webhooks;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<TimerStateDto> Get()
    {
        var entity = await _db.TimerState.FirstOrDefaultAsync()
            ?? new TimerStateEntity();
        var dto = ToDto(entity);
        // Server clock as the shared reference for the remaining-time display on other
        // devices (see TimerStateDto.ServerNow) - DateTime.Now like everywhere else (TZ=Europe/Berlin).
        dto.ServerNow = DateTime.Now;
        return dto;
    }

    /// <summary>
    /// Best effort, last-write-wins by default: no server-side plausibility check between the
    /// fields, and a stale PUT (below) is silently dropped rather than rejected.
    /// </summary>
    [HttpPut]
    public async Task<TimerStateDto> Save(TimerStateDto dto)
    {
        var entity = await _db.TimerState.GetOrCreateAsync(_db);
        var wasRunning = entity.IsRunning;

        // Sequence-based out-of-order rejection (audit S6): TimerService fires this PUT
        // unawaited on every transition, so two rapid transitions can arrive reversed on the
        // wire. A PRESENT ClientSequence smaller than the last one we accepted means this PUT
        // is the OLDER of the two - drop it and hand back the row as it currently stands. This
        // deliberately returns 200 with the current state instead of 409: unlike the settings
        // conflict path (audit S4/S5), there is no interactive caller here to retry against -
        // TimerService's push is fire-and-forget - so a 409 would just be an error nobody looks
        // at. Returning the current row instead is actually MORE useful to a caller like the
        // remote-timer banner (Focus.razor), which polls this same GET/PUT shape and can treat
        // any response uniformly. A MISSING ClientSequence is unconditionally accepted (plain
        // last-write-wins, exactly the behavior before this field existed) - needed for Home
        // Assistant and any other pusher that doesn't know about sequence numbers.
        if (dto.ClientSequence is { } incomingSeq
            && entity.LastClientSequence is { } storedSeq
            && incomingSeq < storedSeq)
        {
            return ToDto(entity);
        }

        // Dangling SessionId (e.g. the session was just deleted while the timer kept running) is
        // silently nulled rather than rejected with 400 - this is the high-frequency, fire-and-
        // forget timer sync path (see the class doc comment above), and a hard failure here would
        // just break the timer push for no one to retry.
        entity.SessionId = dto.SessionId is { } sessionId && await _db.Sessions.AnyAsync(s => s.Id == sessionId)
            ? dto.SessionId
            : null;
        entity.IsRunning = dto.IsRunning;
        entity.IsBreak = dto.IsBreak;
        entity.CurrentRound = dto.CurrentRound;
        entity.TimerModeId = dto.TimerModeId;
        entity.PhaseEndsAt = dto.PhaseEndsAt;
        entity.UpdatedAt = DateTime.Now;
        if (dto.ClientSequence is { } newSeq) entity.LastClientSequence = newSeq;
        await _db.SaveChangesAsync();

        // Fire-and-forget with CancellationToken.None, deliberately NOT the request's own `ct`:
        // this call must outlive the request (a slow/unreachable studylife-webhooks must never
        // add latency to the timer's own high-frequency, latency-sensitive push path), and
        // HttpContext.RequestAborted is not safe for detached work - it can fire the moment the
        // response completes, which would abort an in-flight webhook delivery for no reason.
        // PublishEventAsync itself never throws (see WebhooksProxyClient), so there is no
        // unobserved-exception risk in not awaiting this.
        if (!wasRunning && entity.IsRunning)
        {
            _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.TimerStarted,
                new { sessionId = entity.SessionId }, CancellationToken.None);
        }
        else if (wasRunning && !entity.IsRunning)
        {
            _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.TimerEnded,
                new { sessionId = entity.SessionId }, CancellationToken.None);
        }

        return ToDto(entity);
    }

    private static TimerStateDto ToDto(TimerStateEntity e) => new()
    {
        SessionId = e.SessionId,
        IsRunning = e.IsRunning,
        IsBreak = e.IsBreak,
        CurrentRound = e.CurrentRound,
        TimerModeId = e.TimerModeId,
        PhaseEndsAt = e.PhaseEndsAt,
        UpdatedAt = e.UpdatedAt,
        ClientSequence = e.LastClientSequence,
    };

    /// <summary>Deliberately a SEPARATE endpoint instead of a field on TimerStateDto/Save(): the
    /// normal state push from TimerService (start/pause/stop, runs on every platform incl. web)
    /// doesn't know about this app-only field and would otherwise overwrite it with null on
    /// every call. Only invoked by the app with the push entitlement (paid profile).</summary>
    [HttpPut("liveactivity-token")]
    public async Task<IActionResult> SetLiveActivityPushToken(LiveActivityPushTokenDto dto)
    {
        var entity = await _db.TimerState.GetOrCreateAsync(_db);
        entity.LiveActivityPushToken = dto.Token;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
