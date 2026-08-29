using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

public class TimerService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private System.Threading.Timer? _timer;
    private int _secondsLeft;
    private bool _isRunning;
    private bool _isBreak;
    private int _currentRound;
    private TimerMode? _mode;
    private int? _sessionId;
    private readonly object _lock = new();

    // Wall-clock anchor of the current phase: timer ticks freeze when the operating system
    // suspends the app/tab (locked iPhone, background tab) - a plain "secondsLeft--"
    // counter then loses real time and a 5-minute timer actually takes 7-8 minutes
    // (this became visible in the native app via the Live Activity card; the PWA had the
    // same drift, just invisibly). Tick() therefore computes against this absolute
    // phase end and, upon waking up, also catches up on entirely missed phase transitions.
    // Null = paused/stopped (the frozen _secondsLeft remainder applies).
    private DateTime? _phaseEndsAtUtc;

    // Continuous-focus streak tracking for OnFocusMilestone, kept pause-safe by splitting it
    // into a frozen accumulator (_focusStreakSeconds) plus the wall-clock anchor of the
    // currently-running segment (_focusStreakSegmentStartUtc, null while paused/on break/
    // stopped) - mirrors the _phaseEndsAtUtc pattern so a suspended app catches up correctly
    // instead of a naive "increment by 1 every Tick()" undercounting missed real time.
    private int _focusStreakSeconds;
    private DateTime? _focusStreakSegmentStartUtc;
    private int _focusMilestonesFired;
    public const int FocusMilestoneIntervalMinutes = 25;

    // ── State push: single-flight, latest-wins queue (audit S6) ─────────────────────────────
    // Every call site below used to fire `_ = PushStateAsync()` unawaited on every transition -
    // two rapid transitions (e.g. Start immediately followed by a phase change) could then race
    // on the wire and arrive at the server in either order, leaving a stale state visible until
    // the NEXT transition finally pushed a fresh one. Fix: SchedulePush() never lets two sends
    // run concurrently (a send already in flight just means the next one waits), and a state
    // that arrives while a send is in flight REPLACES whatever was queued rather than queuing
    // a second one - only the single latest state is ever sent next, so sends both never overlap
    // AND never fall behind by more than the one in-flight request. This alone guarantees
    // in-order delivery from a single tab; the ClientSequence sent with every push (below) is
    // what lets the SERVER also reject a stale write if two tabs/devices race each other
    // (TimerStateController.Save - see StudyLife.Shared.TimerStateDto.ClientSequence).
    //
    // Deliberately a SEPARATE lock from _lock: _lock only ever guards building the DTO snapshot
    // (fast, no I/O); _pushLock guards this queue's bookkeeping across the actual PUT (which DOES
    // await network I/O) - sharing _lock here would mean a slow/offline PUT blocks Tick() from
    // ever reading the timer's live state.
    private readonly object _pushLock = new();
    private TimerStateDto? _pendingPush;
    private bool _pushInFlight;
    // Monotonic send order (unix ms) - guarded against clock jumps (DST, NTP correction, a
    // suspended device waking up with a corrected clock) by never going backwards: if the wall
    // clock ever produces a value <= the last one sent, the counter just increments by 1 instead.
    private long _lastSentSequence;

    public event Action<int, bool, int, bool>? OnTick; // secondsLeft, isBreak, round, isRunning
    public event Action? OnSessionComplete;
    public event Action? OnBreakStarted;
    public event Action? OnPaused;
    /// <summary>Fires once per FocusMilestoneIntervalMinutes of continuous, unbroken focus time
    /// (pauses and breaks reset the streak) - a generic "N minutes of sustained focus" signal,
    /// not inherently movement-specific, so other features can subscribe to it later too. The
    /// movement-break nudge (Focus.razor) is its first consumer.</summary>
    public event Action? OnFocusMilestone;
    /// <summary>Fires whenever LoadMode sets a new mode, regardless of caller - lets any open
    /// Focus.razor instance mirror a mode change triggered from elsewhere (e.g. the Watch
    /// companion app's mode picker relaying through WatchTimerCoordinator), not just its own
    /// local LoadMode wrapper.</summary>
    public event Action<TimerMode>? OnModeChanged;

    public TimerService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public int SecondsLeft { get { lock (_lock) return _secondsLeft; } }
    public bool IsRunning { get { lock (_lock) return _isRunning; } }
    public bool IsBreak { get { lock (_lock) return _isBreak; } }
    public int CurrentRound { get { lock (_lock) return _currentRound; } }
    public TimerMode? CurrentMode { get { lock (_lock) return _mode; } }

    public void LoadMode(TimerMode mode, int? sessionId = null)
    {
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _isRunning;
            StopInternal();
            _mode = mode;
            _sessionId = sessionId;
            _currentRound = 1;
            _isBreak = false;
            _secondsLeft = mode.FocusMinutes * 60;
        }
        // Only push if a running timer was actually stopped here. The automatic
        // LoadMode when opening the focus page would otherwise overwrite the running
        // state of ANOTHER device on the server with IsRunning=false (remote banner/Home
        // Assistant would then sporadically see the timer as finished).
        if (wasRunning) SchedulePush();
        OnModeChanged?.Invoke(mode);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_mode == null || _isRunning) return;
            _isRunning = true;
            _phaseEndsAtUtc = DateTime.UtcNow.AddSeconds(_secondsLeft);
            if (!_isBreak) _focusStreakSegmentStartUtc = DateTime.UtcNow;
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        NotifyBrowserOfStateChange();
        SchedulePush();
    }

    public void Pause()
    {
        lock (_lock)
        {
            // Freeze the remainder based on the wall clock - not on the last tick.
            if (_isRunning && _phaseEndsAtUtc is { } endsAt)
                _secondsLeft = Math.Max(0, (int)Math.Ceiling((endsAt - DateTime.UtcNow).TotalSeconds));
            FlushFocusStreakSegment();
            _isRunning = false;
            _phaseEndsAtUtc = null;
            _timer?.Dispose();
            _timer = null;
        }
        OnPaused?.Invoke();
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        NotifyBrowserOfStateChange();
        SchedulePush();
    }

    public void Stop()
    {
        lock (_lock) { StopInternal(); }
        SchedulePush();
    }

    private void StopInternal()
    {
        _isRunning = false;
        _phaseEndsAtUtc = null;
        _timer?.Dispose();
        _timer = null;
        _focusStreakSeconds = 0;
        _focusStreakSegmentStartUtc = null;
        _focusMilestonesFired = 0;
    }

    /// <summary>Folds the currently-running focus segment's elapsed wall-clock time into the
    /// frozen accumulator - called before any transition that stops the segment from running
    /// (pause, break start) so the accumulator always reflects true elapsed focus time.</summary>
    private void FlushFocusStreakSegment()
    {
        if (!_isBreak && _focusStreakSegmentStartUtc is { } segStart)
            _focusStreakSeconds += (int)(DateTime.UtcNow - segStart).TotalSeconds;
        _focusStreakSegmentStartUtc = null;
    }

    public void Reset()
    {
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _isRunning;
            StopInternal();
            if (_mode != null)
            {
                _isBreak = false;
                _currentRound = 1;
                _secondsLeft = _mode.FocusMinutes * 60;
            }
        }
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        // Unconditional, unlike the SchedulePush below: a browser extension reacting to this
        // event only ever re-polls its own authenticated endpoint, so there's no "overwrite
        // another device's state" risk the way there is for the real server push - gating this
        // on wasRunning too would silently disable the instant-reaction path for the overwhelmingly
        // common "pause, then reset" sequence, where wasRunning is false by the time Reset() runs.
        NotifyBrowserOfStateChange();
        // Same as LoadMode: a reset on a device where nothing was running at all must not
        // overwrite the running state of another device on the server.
        if (wasRunning) SchedulePush();
    }

    private void Tick()
    {
        Action? extraEvent = null;
        bool complete = false;
        bool phaseChanged = false;
        bool milestoneReached = false;

        lock (_lock)
        {
            if (!_isRunning || _phaseEndsAtUtc is null) return;

            // Wall clock instead of tick counting (see the _phaseEndsAtUtc comment). The loop
            // also catches up on multiple entirely missed phases after a suspension -
            // each subsequent phase starts at the END of the previous one (not at "now"), so
            // the total duration stays exact. TimerModeCatalog.AdvancePhase (StudyLife.Shared)
            // owns the actual transition math, shared with the server's Live Activity push
            // worker (audit finding D5) - this loop only adds the client-only bookkeeping
            // (focus streak, OnBreakStarted) around each step.
            var now = DateTime.UtcNow;
            var modeData = new TimerModeCatalog.ModeData(_mode!.Id, _mode.Name, _mode.FocusMinutes, _mode.BreakMinutes, _mode.Rounds);
            while (_phaseEndsAtUtc is { } endsAt && now >= endsAt)
            {
                phaseChanged = true;
                var step = TimerModeCatalog.AdvancePhase(modeData, _isBreak, _currentRound, endsAt);
                _currentRound = step.Round;
                if (step.Complete)
                {
                    StopInternal();
                    complete = true;
                    break;
                }
                _isBreak = step.IsBreak;
                _phaseEndsAtUtc = step.PhaseEndsAt;
                if (_isBreak)
                {
                    extraEvent = () => OnBreakStarted?.Invoke();
                    // A break interrupts the streak - fold in what was accumulated up to
                    // step.PreviousPhaseEndsAt (the segment's real end, not "now") and reset for
                    // the next streak.
                    if (_focusStreakSegmentStartUtc is { } segStart)
                        _focusStreakSeconds += (int)(step.PreviousPhaseEndsAt - segStart).TotalSeconds;
                    _focusStreakSegmentStartUtc = null;
                    _focusStreakSeconds = 0;
                    _focusMilestonesFired = 0;
                }
                else
                {
                    // New focus phase begins exactly at the previous phase's end
                    // (step.PreviousPhaseEndsAt), not "now" - keeps the streak's total accurate
                    // across a missed-phase catch-up.
                    _focusStreakSegmentStartUtc = step.PreviousPhaseEndsAt;
                }
            }

            if (!complete && _phaseEndsAtUtc is { } currentEnd)
                _secondsLeft = Math.Max(0, (int)Math.Ceiling((currentEnd - now).TotalSeconds));

            if (!complete && !_isBreak && _focusStreakSegmentStartUtc is { } runningSegStart)
            {
                var totalFocusSeconds = _focusStreakSeconds + (int)(now - runningSegStart).TotalSeconds;
                var milestoneIntervalSeconds = FocusMilestoneIntervalMinutes * 60;
                var reachedMilestones = totalFocusSeconds / milestoneIntervalSeconds;
                if (reachedMilestones > _focusMilestonesFired)
                {
                    _focusMilestonesFired = reachedMilestones;
                    milestoneReached = true;
                }
            }
        }

        if (complete)
        {
            OnSessionComplete?.Invoke();
            SchedulePush();
            return;
        }
        extraEvent?.Invoke();
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        if (milestoneReached) OnFocusMilestone?.Invoke();
        if (phaseChanged) SchedulePush();
    }

    /// <summary>
    /// Reports the current state to the server (only on state changes, not every second), so
    /// external consumers (e.g. Home Assistant) can see whether a focus session is currently
    /// running. Builds the DTO synchronously (fast, no I/O, under _lock) and hands it to the
    /// single-flight push queue below - the caller never awaits the actual network call.
    /// </summary>
    private TimerStateDto BuildStateDto()
    {
        lock (_lock)
        {
            return new TimerStateDto
            {
                SessionId = _sessionId,
                IsRunning = _isRunning,
                IsBreak = _isBreak,
                CurrentRound = _currentRound,
                TimerModeId = _mode?.Id ?? 0,
                // Exact wall-clock end instead of deriving it from the last tick (same
                // local-time semantics as before - consumers like Home Assistant expect it).
                PhaseEndsAt = _isRunning ? _phaseEndsAtUtc?.ToLocalTime() : null,
            };
        }
    }

    private void SchedulePush()
    {
        var dto = BuildStateDto();

        lock (_pushLock)
        {
            dto.ClientSequence = NextSequence();
            // Replaces whatever was queued but not yet sent - only the single latest state
            // survives, regardless of how many transitions fired while a send was in flight.
            _pendingPush = dto;
            if (_pushInFlight) return; // a send is already running; it will pick this up when it loops
            _pushInFlight = true;
        }
        _ = DrainPushQueueAsync();
    }

    /// <summary>Fire-and-forget browser-DOM nudge for installed extensions (see interop.js) -
    /// deliberately separate from SchedulePush above (and from that method's own overwrite-guard
    /// gating at some call sites, e.g. Reset()): an extension reacting to this always re-polls its
    /// own authenticated endpoint rather than trusting the payload, so there is no "stale state
    /// might overwrite another device" risk here the way there is for the real server push, and
    /// this firing (or failing) has no bearing on that push's own success/failure/ordering
    /// guarantees either.</summary>
    private void NotifyBrowserOfStateChange()
    {
        var dto = BuildStateDto();
        DispatchTimerStateChangedEvent(dto);
    }

    private async void DispatchTimerStateChangedEvent(TimerStateDto dto)
    {
        try { await _js.InvokeVoidAsync("dispatchTimerStateChanged", dto); }
        catch { /* JS interop unavailable (e.g. prerendering) - nothing this hook can do about it */ }
    }

    /// <summary>
    /// Sends _pendingPush, then loops: if ANOTHER SchedulePush arrived while that PUT was in
    /// flight, _pendingPush is non-null again by the time the loop re-checks - that gets sent
    /// next before going idle, so sends never overlap (single-flight) but the very latest state
    /// is still guaranteed to eventually reach the server (never silently dropped just because
    /// it arrived mid-send, only ever superseded by something even newer).
    /// </summary>
    private async Task DrainPushQueueAsync()
    {
        while (true)
        {
            TimerStateDto next;
            lock (_pushLock)
            {
                if (_pendingPush == null) { _pushInFlight = false; return; }
                next = _pendingPush;
                _pendingPush = null;
            }
            // Errors are swallowed here exactly as before this fix (no retry queue for timer
            // state, unlike AppStateService's write queue) - if this PUT fails, `next` is simply
            // lost; the next real transition's SchedulePush call will push a fresh state anyway.
            try { await _http.PutAsJsonAsync("api/timerstate", next); }
            catch { /* offline fallback */ }
        }
    }

    /// <summary>Monotonically increasing send order for TimerStateDto.ClientSequence (audit S6):
    /// unix milliseconds, floored at last-sent+1 so a backward clock jump (DST, NTP correction)
    /// can never produce a value the server would see as "older" than a push it already
    /// accepted. Must be called under _pushLock (see SchedulePush) so concurrent callers can't
    /// interleave and momentarily hand out the same value twice.</summary>
    private long NextSequence()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastSentSequence = now > _lastSentSequence ? now : _lastSentSequence + 1;
        return _lastSentSequence;
    }
}
