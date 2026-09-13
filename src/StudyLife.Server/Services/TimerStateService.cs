using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/timerstate domain operations: reading the shared timer row, the fire-and-forget
/// state push with its out-of-order rejection and optimistic-concurrency retry, and the
/// app-only live-activity token registration. <see cref="TimerStateController"/> keeps only
/// route binding - all three endpoints always answer 200, so there is nothing to map.
/// </summary>
public interface ITimerStateService
{
    Task<TimerStateDto> GetAsync();
    Task<TimerStateDto> SaveAsync(TimerStateDto dto);
    Task SetLiveActivityPushTokenAsync(LiveActivityPushTokenDto dto);
}

public class TimerStateService(StudyLifeDb db, WebhooksProxyClient webhooks, ICurrentUserAccessor currentUser) : ITimerStateService
{
    /// <summary>
    /// How many times a write here is re-applied on top of a concurrently changed row before
    /// giving up - see SaveWithReloadAsync. Three is plenty for the only real contender
    /// (BackgroundTaskService.RunLiveActivityPushAsync, which writes this row at most once per
    /// 5s tick and backs off on its own conflict); a higher number would only ever matter
    /// against a writer that is hammering the row faster than we can re-read it, which is not a
    /// shape any caller of this endpoint has.
    /// </summary>
    private const int MaxConcurrencyRetries = 3;

    public async Task<TimerStateDto> GetAsync()
    {
        var entity = await db.TimerState.FirstOrDefaultAsync()
            ?? new TimerStateEntity();
        var dto = ToDto(entity);
        // Server clock as the shared reference for the remaining-time display on other
        // devices (see TimerStateDto.ServerNow) - DateTime.Now like everywhere else (TZ=Europe/Berlin).
        dto.ServerNow = DateTime.Now;
        return dto;
    }

    /// <summary>
    /// Best effort, last-write-wins by default: no server-side plausibility check between the
    /// fields, and a stale push (see TrySaveAsync) is silently dropped rather than rejected.
    ///
    /// "Last-write-wins" is what the optimistic-concurrency retry below preserves rather than
    /// replaces (see TimerStateEntity.RowVersion): a conflicting write from the worker means
    /// this request read the row before the worker's update landed, so the row is re-read and
    /// THIS push is applied on top of it - the client's own start/pause/stop is exactly the
    /// intent that should win over a phase the worker computed from the older state. A 409
    /// would be useless here for the same reason spelled out for the sequence check below:
    /// TimerService fires this PUT unawaited, there is no interactive caller to retry.
    /// </summary>
    public async Task<TimerStateDto> SaveAsync(TimerStateDto dto)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await TrySaveAsync(dto, attempt) is { } result) return result;
        }
    }

    /// <summary>One attempt of <see cref="SaveAsync"/>; null means "the row changed underneath us,
    /// call again". Split out so the whole read-modify-write (including the sequence check and
    /// the wasRunning edge detection, both of which must be judged against the row as it
    /// ACTUALLY stands) is what gets retried, not just the SaveChangesAsync call.</summary>
    private async Task<TimerStateDto?> TrySaveAsync(TimerStateDto dto, int attempt)
    {
        var entity = await db.TimerState.GetOrCreateAsync(db);
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
        // forget timer sync path (see the SaveAsync doc comment above), and a hard failure here
        // would just break the timer push for no one to retry.
        entity.SessionId = dto.SessionId is { } sessionId && await db.Sessions.AnyAsync(s => s.Id == sessionId)
            ? dto.SessionId
            : null;
        entity.IsRunning = dto.IsRunning;
        entity.IsBreak = dto.IsBreak;
        entity.CurrentRound = dto.CurrentRound;
        entity.TimerModeId = dto.TimerModeId;
        // Rebase the deadline onto the server clock when the writer told us its own clock:
        // the other devices compute "remaining" against ServerNow (see GetAsync), so a writer
        // whose clock runs a few seconds ahead used to make their banner start above the full
        // length.
        var now = DateTime.Now;
        entity.PhaseEndsAt = dto.PhaseEndsAt is { } endsAt && dto.ClientNow is { } clientNow
            ? now + (endsAt - clientNow)
            : dto.PhaseEndsAt;
        entity.UpdatedAt = now;
        if (dto.ClientSequence is { } newSeq) entity.LastClientSequence = newSeq;
        if (!await SaveWithReloadAsync(attempt)) return null;

        // Fire-and-forget with CancellationToken.None, deliberately NOT the request's own `ct`:
        // this call must outlive the request (a slow/unreachable studylife-webhooks must never
        // add latency to the timer's own high-frequency, latency-sensitive push path), and
        // HttpContext.RequestAborted is not safe for detached work - it can fire the moment the
        // response completes, which would abort an in-flight webhook delivery for no reason.
        // PublishEventAsync itself never throws (see WebhooksProxyClient), so there is no
        // unobserved-exception risk in not awaiting this.
        if (!wasRunning && entity.IsRunning)
        {
            _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.TimerStarted,
                new { sessionId = entity.SessionId }, CancellationToken.None);
        }
        else if (wasRunning && !entity.IsRunning)
        {
            _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.TimerEnded,
                new { sessionId = entity.SessionId }, CancellationToken.None);
        }

        return ToDto(entity);
    }

    /// <summary>Deliberately a SEPARATE operation instead of a field on TimerStateDto/SaveAsync():
    /// the normal state push from TimerService (start/pause/stop, runs on every platform incl.
    /// web) doesn't know about this app-only field and would otherwise overwrite it with null on
    /// every call. Only invoked by the app with the push entitlement (paid profile).</summary>
    public async Task SetLiveActivityPushTokenAsync(LiveActivityPushTokenDto dto)
    {
        // Same retry-and-re-apply shape as SaveAsync above, and the most important place for it:
        // the worker nulls this very field when it sees an expired token, so a registration
        // racing that write is precisely the update that must not be lost - without it the app
        // would sit with a live activity the server will never push to again.
        for (var attempt = 0; ; attempt++)
        {
            var entity = await db.TimerState.GetOrCreateAsync(db);
            entity.LiveActivityPushToken = dto.Token;
            if (await SaveWithReloadAsync(attempt)) return;
        }
    }

    /// <summary>
    /// Saves the pending change and reports whether the caller may keep its result: false means
    /// another writer got the row first, the conflicting entries were reloaded, and the caller
    /// should re-apply its change on top of the fresh row. The final attempt deliberately lets
    /// DbUpdateConcurrencyException escape to the ProblemDetails handler (500) instead of
    /// pretending the write succeeded - silently dropping a start/stop would leave the client
    /// and the server disagreeing about whether the timer is running, which is exactly the bug
    /// class this token exists to catch.
    /// </summary>
    private async Task<bool> SaveWithReloadAsync(int attempt)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException ex) when (attempt < MaxConcurrencyRetries)
        {
            // Re-reading via the DbSet would NOT refresh anything: EF's identity resolution
            // keeps the already-tracked instance's values, so the stale row would be re-applied
            // unchanged and conflict again. ReloadAsync is what actually replaces current AND
            // original values with the database's (or detaches the entry if the row is gone -
            // GetOrCreateAsync then simply inserts a new one on the next attempt).
            foreach (var entry in ex.Entries) await entry.ReloadAsync();
            return false;
        }
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
}
