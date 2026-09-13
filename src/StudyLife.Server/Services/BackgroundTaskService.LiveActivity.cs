using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    /// <summary>
    /// Step D (Live Activity push): runs on EVERY tick unconditionally, not gated like
    /// RunPushNotificationsAsync/RunCaptureEnrichmentAsync (see their own 30s gates) - phase
    /// transitions need to arrive promptly, otherwise the lock-screen card shows "stale" longer
    /// than necessary
    /// (staleDate grace period in LiveActivityBridge.swift, see there). The "power switch" gate
    /// is ApnsSender.Enabled - without Apns config (free tier or config missing) this is a
    /// silent no-op, TimerState/LiveActivityPushToken remain untouched.
    ///
    /// Independently recomputes the phase-transition state machine from TimerService.Tick()
    /// (client): the device is locked/suspended in the meantime, so the client itself can no
    /// longer tick. Both sides call TimerModeCatalog.AdvancePhase (StudyLife.Shared) for the
    /// actual transition math (audit finding D5) - any deviation would make the client and
    /// server displays drift apart.
    /// </summary>
    internal async Task RunLiveActivityPushAsync(StudyLifeDb db)
    {
        if (!_apnsSender.Enabled) return;

        var state = await db.TimerState.FirstOrDefaultAsync();
        if (state is not { IsRunning: true, PhaseEndsAt: { } phaseEndsAt }) return;
        if (state.LiveActivityPushToken is not { Length: > 0 } token) return;

        // Local server time as everywhere else in the timer context (TimerStateService.GetAsync/
        // PushStateAsync in the client) - PhaseEndsAt was written in the same time base.
        var now = LocalNow;
        if (now < phaseEndsAt) return;

        var settings = await db.Settings.FirstOrDefaultAsync();
        var mode = ServerTimerModes.Resolve(state.TimerModeId, settings?.CustomTimerModes);
        if (mode == null) return; // Mode deleted/unknown - no crash, just skip the push

        var isBreak = state.IsBreak;
        var round = state.CurrentRound;
        var endsAt = phaseEndsAt;
        var complete = false;
        var modeData = new TimerModeCatalog.ModeData(mode.Id, mode.Name, mode.FocusMinutes, mode.BreakMinutes, mode.Rounds);

        // Same stepper as TimerService.Tick() (StudyLife.Shared.TimerModeCatalog.AdvancePhase):
        // also catches up on multiple entirely missed phases (e.g. if the worker tick itself was
        // delayed).
        while (now >= endsAt)
        {
            var step = TimerModeCatalog.AdvancePhase(modeData, isBreak, round, endsAt);
            isBreak = step.IsBreak;
            round = step.Round;
            endsAt = step.PhaseEndsAt;
            if (step.Complete) { complete = true; break; }
        }

        if (complete)
        {
            var outcome = await _apnsSender.SendLiveActivityEndAsync(token,
                new DateTimeOffset(endsAt), isBreak, secondsLeft: 0,
                phaseTotalSeconds: 0, round: mode.Rounds, totalRounds: mode.Rounds);
            // On a transient failure (Apple's sandbox environment is occasionally slow/
            // unreliable) save NOTHING - IsRunning/PhaseEndsAt stay at the old (already
            // expired) state, so the next tick (5s later) retries the same transition instead
            // of silently swallowing it.
            if (outcome == ApnsSendOutcome.Failed) return;
            state.IsRunning = false;
            if (outcome == ApnsSendOutcome.ExpiredToken) state.LiveActivityPushToken = null;
            await TrySaveTimerStateAsync(db, state);
            return;
        }

        var secondsLeft = Math.Max(0, (int)(endsAt - now).TotalSeconds);
        var phaseTotalSeconds = (isBreak ? mode.BreakMinutes : mode.FocusMinutes) * 60;
        var updateOutcome = await _apnsSender.SendLiveActivityUpdateAsync(token,
            new DateTimeOffset(endsAt), isBreak, secondsLeft, phaseTotalSeconds, round, mode.Rounds);

        // Same principle as above: only adopt the new phase state on a definitive outcome
        // (delivered OR token permanently invalid). A plain "Failed" leaves PhaseEndsAt in the
        // past - the next tick sends the same transition again instead of skipping it (observed
        // live: the card stayed frozen at 0:00 until the FOLLOWING phase expired, because the
        // failure caused the actual transition to be skipped).
        if (updateOutcome == ApnsSendOutcome.Failed) return;

        state.IsBreak = isBreak;
        state.CurrentRound = round;
        state.PhaseEndsAt = endsAt;
        if (updateOutcome == ApnsSendOutcome.ExpiredToken) state.LiveActivityPushToken = null;
        await TrySaveTimerStateAsync(db, state);
    }

    /// <summary>
    /// Persists this tick's phase transition, treating an optimistic-concurrency conflict as
    /// "the client already moved on". The read at the top of RunLiveActivityPushAsync is
    /// separated from this write by an outbound APNs call (seconds, on an unreliable network),
    /// and web and worker are separate processes - so a user pressing pause/stop, or the app
    /// registering a fresh live-activity token, can land in between. Re-applying our computed
    /// phase on top of that would resurrect a timer the user just stopped, or blank a token that
    /// is seconds old (see TimerStateEntity.RowVersion). Dropping this tick's write costs
    /// nothing: the next tick is 5s away and recomputes the state machine from the row as it
    /// then actually stands. Deliberately no retry loop here, unlike TimerStateController -
    /// retrying would re-assert exactly the stale decision the conflict just rejected.
    /// </summary>
    private async Task TrySaveTimerStateAsync(StudyLifeDb db, TimerStateEntity state)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Detach, don't just swallow: ExecuteAsync shares ONE DbContext across all sub-tasks
            // of a tick, so a rejected UPDATE left pending here would be replayed (and throw
            // again) inside whichever unrelated sub-task saves next.
            db.Entry(state).State = EntityState.Detached;
            _logger.LogDebug(
                "Live Activity: timer state for user {AuthUserId} changed while the APNs push was in flight - skipping this tick's update",
                _currentAuthUserId);
        }
    }
}
