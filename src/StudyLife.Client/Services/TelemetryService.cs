using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// Phase 2 of the telemetry plan (docs/ARCHITECTURE.md "Telemetry"): buffers client-side events
/// and flushes them to POST /api/telemetry every 20s or at 25 events, plus a best-effort
/// <c>navigator.sendBeacon</c> flush on pagehide/visibilitychange (see js/interop.js -
/// studylifeTelemetryFlush/GetPendingTelemetryJson - a beacon flush must be synchronous from the
/// JS side, so it reads the buffer through <see cref="GetPendingTelemetryJson"/> rather than an
/// awaited round trip that the browser could kill mid-flight).
///
/// Scoped (registered in Program.cs like every other per-app-instance service) - but since this
/// is a single-page browser app there is only ever one real instance per tab, which is why the
/// static JSInvokable entry points below (called via plain JS function names, not a
/// DotNetObjectReference, so they can run before/without an async round trip) route through
/// <see cref="_current"/> instead of needing one.
///
/// Consent (UserSettingsEntity.TelemetryConsent) is enforced on BOTH ends deliberately: the
/// server already drops an unconsented batch with 204 and records nothing, but the client also
/// never buffers device/performance data for someone who has declined (and discards whatever it
/// already buffered on decline) - undecided (null) keeps buffering in memory so an eventual
/// accept doesn't lose the whole session's boot mesurements, matching the contract's explicit
/// "boot events collected before the consent answer are kept ... or discarded on decline".
///
/// Sampling: one coin flip per SESSION (not per event) - see EnsureSessionAsync - so a boot and
/// its own follow-up API calls either both appear or neither does, keeping correlation intact.
/// <c>error</c> events always bypass sampling (but never the consent gate above).
/// </summary>
public sealed class TelemetryService : IAsyncDisposable
{
    private const int FlushIntervalSeconds = 20;
    private const int FlushEventThreshold = 25;
    private const int MaxEventsPerBatch = 50;
    private const string SessionStorageKey = "studylife-telemetry-session";
    // Server-provided (api/system/capabilities, Telemetry:ClientSampleRatio); 0.10 until the
    // capabilities call answers, and forever if it fails.
    private double _sampleRate = 0.10;

    private static readonly HashSet<string> SseEventKinds = new(StringComparer.Ordinal) { "connected", "reconnect", "fallback_poll" };

    private static TelemetryService? _current;

    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private readonly AppStateService _state;
    private readonly INativeTelemetry _native;
    private readonly IClientPlatform _platform;
    private readonly Toolbelt.Blazor.I18nText.I18nText _i18nText;

    private readonly List<TelemetryEventDto> _buffer = new();
    private readonly object _bufferLock = new();
    private Timer? _flushTimer;
    private string _sessionId = "";
    // null until EnsureSessionAsync has run (the sample rate comes from the server, see
    // InitializeAsync): events recorded before that are buffered, not dropped, because
    // MainLayout starts the whole app without waiting for this service.
    private bool? _sampled;
    private bool? _consent;
    private string _language = "en";
    private bool _bootMarksRead;

    public TelemetryService(HttpClient http, IJSRuntime js, AppStateService state, INativeTelemetry native,
        IClientPlatform platform, Toolbelt.Blazor.I18nText.I18nText i18nText)
    {
        _http = http;
        _js = js;
        _state = state;
        _native = native;
        _platform = platform;
        _i18nText = i18nText;
        _current = this;
        _state.OnSettingsChanged += OnSettingsChanged;
        _state.OnSseLifecycleEventRaised += OnSseLifecycleEvent;
        // The stream may already be up (it starts as soon as a token exists, this service is
        // constructed later) - count that connection too, otherwise a fast host never reports one.
        if (_state.ChangeStreamConnected) OnSseLifecycleEvent("connected", 0);
    }

    /// <summary>Call once at app start (MainLayout.OnInitializedAsync, after settings are first
    /// loaded) - establishes the session id/sampling decision, the consent snapshot, the flush
    /// timer and the JS-side pagehide/visibilitychange/window.onerror hooks.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var capabilities = await _http.GetFromJsonAsync<SystemCapabilitiesResponseDto>(
                $"api/system/capabilities?nocache={DateTime.UtcNow.Ticks}", StudyLifeJson.Options);
            if (capabilities is not null)
                _sampleRate = Math.Clamp(capabilities.TelemetryClientSampleRatio, 0, 1);
        }
        catch { /* keep the default - never block startup on this */ }
        await EnsureSessionAsync();
        try
        {
            var settings = await _state.GetSettingsAsync();
            _consent = settings.TelemetryConsent;
            _language = await _i18nText.GetCurrentLanguageAsync();
        }
        catch { /* best-effort - telemetry must never block app startup */ }

        _flushTimer = new Timer(async _ => await FlushAsync(),
            null, TimeSpan.FromSeconds(FlushIntervalSeconds), TimeSpan.FromSeconds(FlushIntervalSeconds));

        try { await _js.InvokeVoidAsync("studylifeTelemetryInit"); }
        catch { /* JS helper unavailable (stale cached index.html, or a test host) - buffering/timer flush still work */ }
    }

    /// <summary>24h rotation, one coin flip per session persisted alongside the id so a page
    /// reload within the same session keeps the same sampling decision (contract: "a per-session
    /// coin flip, so one session is either fully sampled or not"). Never derived from the user id.</summary>
    private async Task EnsureSessionAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SessionStorageKey);
            var stored = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<StoredSession>(json);
            var ageOk = stored is not null
                && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(stored.CreatedAt) < TimeSpan.FromHours(24);
            // A stored coin flip only stays valid for the rate it was flipped at - when the
            // operator changes Telemetry:ClientSampleRatio the next page load re-rolls instead of
            // honouring a decision made under the old rate for up to 24 h.
            var rateOk = stored is not null && Math.Abs(stored.Rate - _sampleRate) < 0.0001;
            if (stored is { Id.Length: > 0 } && ageOk && rateOk)
            {
                _sessionId = stored.Id;
                _sampled = stored.Sampled;
                ApplySamplingDecision();
                return;
            }
        }
        catch { /* corrupt/unavailable storage - fall through to a fresh session */ }

        _sessionId = GenerateSessionId();
        _sampled = Random.Shared.NextDouble() < _sampleRate;
        ApplySamplingDecision();
        try
        {
            var json = JsonSerializer.Serialize(new StoredSession(_sessionId, _sampled == true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _sampleRate));
            await _js.InvokeVoidAsync("localStorage.setItem", SessionStorageKey, json);
        }
        catch { /* best-effort persistence; the session still works for the rest of this page load */ }
    }

    /// <summary>Once the coin flip is known, a sampled-out session keeps only its `error`
    /// events (which always bypass sampling) from what was buffered before the decision.</summary>
    private void ApplySamplingDecision()
    {
        if (_sampled != false) return;
        lock (_bufferLock) _buffer.RemoveAll(e => e.Type != "error");
    }

    private sealed record StoredSession(string Id, bool Sampled, long CreatedAt, double Rate = 0.10);

    // 22 chars of a 128-bit random value, base64url-ish alphabet restricted to the contract's
    // [A-Za-z0-9_-]{16,32} - well within bounds and collision-safe enough for a correlation id.
    private static string GenerateSessionId()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ── Recording API (called from SessionHandler, MainLayout, App's ErrorBoundary, JS callbacks) ──

    public void RecordBoot(bool cold, double? htmlMs, double? bootScriptMs, double? wasmDownloadMs,
        double? runtimeReadyMs, double? firstRenderMs, double? dashboardReadyMs, double? downloadBytes, bool swCacheHit) =>
        Enqueue(new TelemetryEventDto
        {
            Type = "boot",
            Cold = cold,
            HtmlMs = htmlMs,
            BootScriptMs = bootScriptMs,
            WasmDownloadMs = wasmDownloadMs,
            RuntimeReadyMs = runtimeReadyMs,
            FirstRenderMs = firstRenderMs,
            DashboardReadyMs = dashboardReadyMs,
            DownloadBytes = downloadBytes,
            SwCacheHit = swCacheHit,
        });

    public void RecordVitals(double? ttfb, double? fcp, double? lcp, double? inp, double? cls) =>
        Enqueue(new TelemetryEventDto { Type = "vitals", Ttfb = ttfb, Fcp = fcp, Lcp = lcp, Inp = inp, Cls = cls });

    /// <summary>Called from SessionHandler for every request to this app's own API. Route MUST
    /// already be a template (e.g. "api/sessions/{id}") - SessionHandler derives it from the
    /// request's own RequestUri, replacing numeric/GUID segments, mirroring (client-side) the
    /// same normalization TelemetryController applies server-side.</summary>
    public void RecordApi(string route, string method, int status, double durationMs, bool notModified, int retries) =>
        Enqueue(new TelemetryEventDto
        {
            Type = "api",
            Route = route,
            Method = method,
            Status = status,
            DurationMs = durationMs,
            NotModified = notModified,
            Retries = retries,
        });

    private void OnSseLifecycleEvent(string kind, double durationMs)
    {
        if (!SseEventKinds.Contains(kind)) return;
        Enqueue(new TelemetryEventDto { Type = "sse", Event = kind, DurationMs = durationMs });
    }

    public void RecordNavigation(string page, double renderMs) =>
        Enqueue(new TelemetryEventDto { Type = "navigation", Page = page, RenderMs = renderMs });

    /// <summary>kind: dotnet | js | native_crash | native_hang | native_anr. Always sent
    /// regardless of session sampling (contract: "error always") - still subject to the consent
    /// gate like every other event. Stack is sanitized/hashed here so BOTH the .NET ErrorBoundary
    /// path and the JS window.onerror path (ReportJsError below) go through one implementation.</summary>
    public void RecordError(string kind, string exceptionType, string? rawStack, bool fatal, string? page)
    {
        var (stack, stackHash) = SanitizeAndHashStack(rawStack);
        Enqueue(new TelemetryEventDto
        {
            Type = "error",
            Kind = kind,
            ErrorType = exceptionType,
            Stack = stack,
            StackHash = stackHash,
            Fatal = fatal,
            Page = page,
        }, alwaysSend: true);
    }

    /// <summary>Sanitizes a stack trace to the contract's rules (no query strings, no message
    /// text) and returns it alongside its SHA-256 hex hash. A stack's first line is conventionally
    /// "ExceptionType: message" (both .NET's ToString() and JS's Error.stack) - dropped
    /// unconditionally since the exception TYPE already travels as its own field and the message
    /// text must never leave the device (may contain user content/PII).</summary>
    internal static (string Stack, string StackHash) SanitizeAndHashStack(string? rawStack)
    {
        if (string.IsNullOrWhiteSpace(rawStack)) return ("", ComputeSha256Hex(""));
        var lines = rawStack.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Contains(':') && (l.TrimStart().StartsWith("at ", StringComparison.Ordinal) || l.Contains(".cs:") || l.Contains(".js:")))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\?[^\s:)]*", ""));
        var sanitized = string.Join('\n', lines);
        if (sanitized.Length > 4096) sanitized = sanitized[..4096];
        return (sanitized, ComputeSha256Hex(sanitized));
    }

    private static string ComputeSha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Read once after the first real render (MainLayout.OnAfterRenderAsync) - the
    /// contract's boot phases come from performance.mark timestamps js/boot-loading.js and
    /// index.html's manual Blazor.start() already recorded during boot.</summary>
    public async Task RecordBootFromMarksAsync()
    {
        if (_bootMarksRead) return;
        _bootMarksRead = true;
        try
        {
            var marks = await _js.InvokeAsync<BootMarks?>("studylifeGetBootMarks");
            if (marks is null) return;
            RecordBoot(marks.Cold, marks.HtmlMs, marks.BootScriptMs, marks.WasmDownloadMs,
                marks.RuntimeReadyMs, marks.FirstRenderMs, dashboardReadyMs: null,
                marks.DownloadBytes, marks.SwCacheHit);
        }
        catch { /* stale cached index.html without the helper, or marks unsupported - skip silently */ }
    }

    private sealed record BootMarks(bool Cold, double? HtmlMs, double? BootScriptMs, double? WasmDownloadMs,
        double? RuntimeReadyMs, double? FirstRenderMs, double? DownloadBytes, bool SwCacheHit);

    /// <summary>"Dashboard has real data" milestone - approximated here as "the first settings
    /// fetch after app start completed", a natural, already-awaited hook point (MainLayout awaits
    /// GetSettingsAsync during its own OnInitializedAsync) rather than instrumenting Index.razor's
    /// internals directly, which would risk destabilizing the actual dashboard page for a
    /// secondary, instrumentation-only concern.</summary>
    public void RecordDashboardReady(double dashboardReadyMs) =>
        Enqueue(new TelemetryEventDto { Type = "boot", DashboardReadyMs = dashboardReadyMs, Cold = false });

    private void Enqueue(TelemetryEventDto ev, bool alwaysSend = false)
    {
        if (_consent == false) return; // declined - never even buffer
        if (!alwaysSend && _sampled == false) return; // sampled-out session - only `error` bypasses this; null = undecided, buffer
        bool shouldFlush;
        lock (_bufferLock)
        {
            ev.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _buffer.Add(ev);
            shouldFlush = _buffer.Count >= FlushEventThreshold;
        }
        if (shouldFlush) _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        if (_consent != true) return; // undecided: keep buffering without sending; declined: nothing to buffer anyway
        if (_sampled is null) return; // session not decided yet (InitializeAsync still running) - keep buffering

        List<TelemetryEventDto> toSend;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0) return;
            toSend = new List<TelemetryEventDto>(_buffer);
            _buffer.Clear();
        }

        if (_native.IsAvailable)
        {
            try
            {
                var native = await _native.DrainAsync();
                if (native is { Count: > 0 }) toSend.AddRange(native);
            }
            catch { /* native bridge failure must never lose the events already collected here */ }
        }

        if (toSend.Count > MaxEventsPerBatch) toSend = toSend[..MaxEventsPerBatch];

        try
        {
            await _http.PostAsJsonAsync("api/telemetry", await BuildBatchAsync(toSend), StudyLifeJson.Options);
        }
        catch { /* offline/error - a lost telemetry batch is never worth retrying at the cost of complexity */ }
    }

    private async Task<TelemetryBatchDto> BuildBatchAsync(List<TelemetryEventDto> events)
    {
        var connection = "unknown";
        try { connection = await _js.InvokeAsync<string>("studylifeGetConnectionType"); }
        catch { /* helper unavailable - "unknown" is a valid contract value */ }

        return new TelemetryBatchDto
        {
            SessionId = _sessionId,
            Platform = _platform.Name,
            AppVersion = GetAppVersion(),
            Language = _language,
            Connection = connection,
            Events = events,
        };
    }

    private static string GetAppVersion() =>
        typeof(TelemetryService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "dev";

    private void OnSettingsChanged() => _ = HandleConsentChangeAsync();

    private async Task HandleConsentChangeAsync()
    {
        bool? previous;
        try
        {
            var settings = await _state.GetSettingsAsync();
            previous = _consent;
            _consent = settings.TelemetryConsent;
        }
        catch { return; }

        if (_consent == true && previous != true)
            await FlushAsync(); // just accepted (or undecided->true on first load) - send whatever was buffered so far
        else if (_consent == false)
            lock (_bufferLock) _buffer.Clear(); // declined - discard, never send device data for a decline
    }

    // ── JS entry points ──────────────────────────────────────────────────────

    /// <summary>Called SYNCHRONOUSLY from js/interop.js (DotNet.invokeMethod, not invokeMethodAsync)
    /// on pagehide/visibilitychange - an async round trip started that late routinely never
    /// resolves before the tab is actually gone. Clears the buffer unconditionally on return (a
    /// beacon delivery failure is not retried, same "best effort" stance as the timer flush)
    /// and returns null (nothing to send) whenever consent isn't a confirmed `true`.</summary>
    [JSInvokable]
    public static string? GetPendingTelemetryJson()
    {
        var self = _current;
        if (self is null || self._consent != true || self._sampled is null) return null;
        List<TelemetryEventDto> toSend;
        lock (self._bufferLock)
        {
            if (self._buffer.Count == 0) return null;
            toSend = new List<TelemetryEventDto>(self._buffer);
            self._buffer.Clear();
        }
        if (toSend.Count > MaxEventsPerBatch) toSend = toSend[..MaxEventsPerBatch];
        // Connection/native-drain are deliberately skipped here (both need an async round trip
        // this synchronous path cannot afford) - the beacon batch is otherwise identical.
        var batch = new TelemetryBatchDto
        {
            SessionId = self._sessionId,
            Platform = self._platform.Name,
            AppVersion = GetAppVersion(),
            Language = self._language,
            Connection = "unknown",
            Events = toSend,
        };
        return JsonSerializer.Serialize(batch, StudyLifeJson.Options);
    }

    /// <summary>window.onerror/unhandledrejection (js/interop.js) - "type" is the JS error's
    /// constructor name (e.g. "TypeError"), never its message.</summary>
    [JSInvokable]
    public static void ReportJsError(string errorType, string? stack, bool fatal, string? page) =>
        _current?.RecordError("js", errorType, stack, fatal, page);

    /// <summary>Web Vitals (js/interop.js PerformanceObserver block), reported once on
    /// pagehide/visibilitychange like the beacon flush itself - LCP/CLS/INP only finalize once
    /// the page is backgrounded. Any argument may be null (e.g. INP needs at least one
    /// interaction to exist at all).</summary>
    [JSInvokable]
    public static void ReportVitals(double? ttfb, double? fcp, double? lcp, double? inp, double? cls) =>
        _current?.RecordVitals(ttfb, fcp, lcp, inp, cls);

    public async ValueTask DisposeAsync()
    {
        _state.OnSettingsChanged -= OnSettingsChanged;
        _state.OnSseLifecycleEventRaised -= OnSseLifecycleEvent;
        if (_flushTimer != null) await _flushTimer.DisposeAsync();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
