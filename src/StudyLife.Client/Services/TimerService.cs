using System.Net.Http.Json;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

public class TimerService
{
    private readonly HttpClient _http;
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

    public event Action<int, bool, int, bool>? OnTick; // secondsLeft, isBreak, round, isRunning
    public event Action? OnSessionComplete;
    public event Action? OnBreakStarted;
    public event Action? OnPaused;
    /// <summary>Fires whenever LoadMode sets a new mode, regardless of caller - lets any open
    /// Focus.razor instance mirror a mode change triggered from elsewhere (e.g. the Watch
    /// companion app's mode picker relaying through WatchTimerCoordinator), not just its own
    /// local LoadMode wrapper.</summary>
    public event Action<TimerMode>? OnModeChanged;

    public TimerService(HttpClient http) => _http = http;

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
        if (wasRunning) _ = PushStateAsync();
        OnModeChanged?.Invoke(mode);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_mode == null || _isRunning) return;
            _isRunning = true;
            _phaseEndsAtUtc = DateTime.UtcNow.AddSeconds(_secondsLeft);
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        _ = PushStateAsync();
    }

    public void Pause()
    {
        lock (_lock)
        {
            // Freeze the remainder based on the wall clock - not on the last tick.
            if (_isRunning && _phaseEndsAtUtc is { } endsAt)
                _secondsLeft = Math.Max(0, (int)Math.Ceiling((endsAt - DateTime.UtcNow).TotalSeconds));
            _isRunning = false;
            _phaseEndsAtUtc = null;
            _timer?.Dispose();
            _timer = null;
        }
        OnPaused?.Invoke();
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        _ = PushStateAsync();
    }

    public void Stop()
    {
        lock (_lock) { StopInternal(); }
        _ = PushStateAsync();
    }

    private void StopInternal()
    {
        _isRunning = false;
        _phaseEndsAtUtc = null;
        _timer?.Dispose();
        _timer = null;
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
        // Same as LoadMode: a reset on a device where nothing was running at all must not
        // overwrite the running state of another device on the server.
        if (wasRunning) _ = PushStateAsync();
    }

    private void Tick()
    {
        Action? extraEvent = null;
        bool complete = false;
        bool phaseChanged = false;

        lock (_lock)
        {
            if (!_isRunning || _phaseEndsAtUtc is null) return;

            // Wall clock instead of tick counting (see the _phaseEndsAtUtc comment). The loop
            // also catches up on multiple entirely missed phases after a suspension -
            // each subsequent phase starts at the END of the previous one (not at "now"), so
            // the total duration stays exact.
            var now = DateTime.UtcNow;
            while (_phaseEndsAtUtc is { } endsAt && now >= endsAt)
            {
                phaseChanged = true;
                if (_isBreak)
                {
                    _currentRound++;
                    if (_mode != null && _currentRound > _mode.Rounds)
                    {
                        StopInternal();
                        complete = true;
                        break;
                    }
                    _isBreak = false;
                    _phaseEndsAtUtc = endsAt.AddSeconds(_mode!.FocusMinutes * 60);
                }
                else
                {
                    _isBreak = true;
                    _phaseEndsAtUtc = endsAt.AddSeconds(_mode!.BreakMinutes * 60);
                    extraEvent = () => OnBreakStarted?.Invoke();
                }
            }

            if (!complete && _phaseEndsAtUtc is { } currentEnd)
                _secondsLeft = Math.Max(0, (int)Math.Ceiling((currentEnd - now).TotalSeconds));
        }

        if (complete)
        {
            OnSessionComplete?.Invoke();
            _ = PushStateAsync();
            return;
        }
        extraEvent?.Invoke();
        OnTick?.Invoke(SecondsLeft, IsBreak, CurrentRound, IsRunning);
        if (phaseChanged) _ = PushStateAsync();
    }

    /// <summary>
    /// Reports the current state to the server (only on state changes, not
    /// every second), so external consumers (e.g. Home Assistant) can see whether
    /// a focus session is currently running. Errors are swallowed, analogous to the
    /// offline fallback in AppStateService.
    /// </summary>
    private async Task PushStateAsync()
    {
        TimerStateDto dto;
        lock (_lock)
        {
            dto = new TimerStateDto
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
        try { await _http.PutAsJsonAsync("api/timerstate", dto); }
        catch { /* offline fallback */ }
    }
}
