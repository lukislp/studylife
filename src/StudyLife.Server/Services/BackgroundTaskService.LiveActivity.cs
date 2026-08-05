using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    /// <summary>
    /// Step D (Live Activity push): runs on EVERY tick unconditionally (like
    /// RunPushNotificationsAsync), not behind an hourly gate - phase transitions need to
    /// arrive promptly, otherwise the lock-screen card shows "stale" longer than necessary
    /// (staleDate grace period in LiveActivityBridge.swift, see there). The "power switch" gate
    /// is ApnsSender.Enabled - without Apns config (free tier or config missing) this is a
    /// silent no-op, TimerState/LiveActivityPushToken remain untouched.
    ///
    /// Independently recomputes the phase-transition state machine from TimerService.Tick()
    /// (client): the device is locked/suspended in the meantime, so the client itself can no
    /// longer tick. Same formula, same round counting - any deviation would make the client and
    /// server displays drift apart.
    /// </summary>
    internal async Task RunLiveActivityPushAsync(StudyLifeDb db)
    {
        if (!_apnsSender.Enabled) return;

        var state = await db.TimerState.FirstOrDefaultAsync();
        if (state is not { IsRunning: true, PhaseEndsAt: { } phaseEndsAt }) return;
        if (state.LiveActivityPushToken is not { Length: > 0 } token) return;

        // Local server time as everywhere else in the timer context (TimerStateController.Get/
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

        // Identical loop to TimerService.Tick(): also catches up on multiple entirely missed
        // phases (e.g. if the worker tick itself was delayed).
        while (now >= endsAt)
        {
            if (isBreak)
            {
                round++;
                if (round > mode.Rounds) { complete = true; break; }
                isBreak = false;
                endsAt = endsAt.AddSeconds(mode.FocusMinutes * 60);
            }
            else
            {
                isBreak = true;
                endsAt = endsAt.AddSeconds(mode.BreakMinutes * 60);
            }
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
            await db.SaveChangesAsync();
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
        await db.SaveChangesAsync();
    }
}
