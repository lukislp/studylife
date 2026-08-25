using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

public class AppStateService : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<AppStateService> _logger;
    private readonly SessionTokenStore _sessionTokenStore;
    private UserSettings? _settingsCache;
    private string _settingsHash = "";
    private List<StudySession>? _sessionsCache;
    private string _sessionsHash = "";
    private List<CourseDto>? _coursesCache;
    /// <summary>Program id that _coursesCache is valid for (null = built-in program).</summary>
    private int? _coursesCacheProgramId;
    private Dictionary<string, int>? _groupQuotasCache;
    private int? _groupQuotasCacheProgramId;
    private Timer? _refreshTimer;

    // ── Offline write queue ──────────────────────────────────────────────────
    // If a write fails with an exception (typically: offline), it gets
    // enqueued here, persisted to localStorage, and replayed in original order
    // on the next poll. Non-success responses (e.g. 400 due to
    // EndTime <= StartTime) are deliberately NOT enqueued on the initial attempt -
    // those would keep failing forever on replay. Replay itself applies the same
    // distinction to responses it gets back (see TryReplayEntryAsync): a definitive
    // rejection is discarded, but a 401/403/408/429/5xx is treated like a network
    // error - the entry is kept and replay stops, so an expired session or a
    // transient server hiccup can never drain the queue.
    private const string QueueStorageKey = "studylife-write-queue";
    private const string TypeSaveSession = "saveSession";
    private const string TypeDeleteSession = "deleteSession";
    private const string TypeDeleteSeries = "deleteSeries";
    private const string TypeSaveSettings = "saveSettings";
    private const string TypeSaveNote = "saveNote";
    private const string TypeDeleteNote = "deleteNote";
    private const string TypeSaveCourseGoal = "saveCourseGoal";
    private List<QueuedWrite> _writeQueue = new();
    private bool _queueLoaded;
    private bool _replaying;
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    private sealed record QueuedWrite
    {
        public string Type { get; init; } = "";
        /// <summary>Serialized DTO (saveSession/saveSettings), Id as string (deleteSession), or DeleteSeriesPayload JSON (deleteSeries).</summary>
        public string Payload { get; init; } = "";
        public DateTime QueuedAt { get; init; }
    }

    private sealed record DeleteSeriesPayload(string GroupId, DateTime? FromDate);

    // ── Offline read cache ───────────────────────────────────────────────────
    // Last successful server state of settings/sessions/courses in localStorage,
    // so a cold start with no signal doesn't end up with empty lists. IMPORTANT: it's read
    // EXCLUSIVELY in the catch of a genuine server attempt (actually offline) -
    // when online, the server always has the last word, there is no cache-first path.
    // "-v2" suffix (2026-07-21): bug found live - this cache has no invalidation on logout
    // (SessionTokenStore.ClearAsync only clears the auth token) and no staleness check, so a
    // fetch that silently keeps failing (never surfaced anywhere - the catch below logs nothing)
    // pins the UI to a frozen snapshot indefinitely, surviving app restarts and re-logins.
    // Confirmed live: the stats heatmap stayed frozen on a specific day's data on the native app
    // even after a full re-login, while a direct authenticated curl against the same endpoint
    // returned correct, current data - the server was never the problem. The version bump
    // orphans every existing cache entry so all installs get exactly one forced fresh fetch;
    // the underlying "why did it keep failing" is deliberately not chased further here, since it
    // no longer matters once the cache can't wedge itself into permanent staleness.
    private const string ReadCacheKeySettings = "studylife-cache-settings-v2";
    private const string ReadCacheKeySessions = "studylife-cache-sessions-v2";
    private const string ReadCacheKeyCourses = "studylife-cache-courses-v2";

    private sealed record CachedCourses(int? ProgramId, List<CourseDto> Courses);

    /// <summary>Public so pages with their own data management (e.g. Notes.razor) can
    /// also use the same offline read cache mechanism - same rule applies there:
    /// read ONLY in the error path of a genuine server attempt.</summary>
    public async Task StoreReadCacheAsync(string key, object payload)
    {
        try { await _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(payload)); }
        catch { /* localStorage not available - cache is purely a nice-to-have */ }
    }

    public async Task<T?> LoadReadCacheAsync<T>(string key) where T : class
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);
        }
        catch { return null; }
    }

    /// <summary>
    /// GET with an offline read cache for pages' own loading fetches (dashboard history,
    /// course goals, notes on the stats page, etc.): network first, success gets cached
    /// under the URL, the cache is read ONLY in the error path. This means a page offline
    /// no longer throws an exception into the error boundary ("failed to load"), but instead
    /// shows the last server state - null only if there's no cache either.
    /// "-v2" suffix: see the ReadCacheKeySettings/Sessions/Courses comment above - same
    /// silent-staleness bug applied here too, since every GetJsonCachedAsync caller shares this
    /// exact mechanism. The exception is now logged (it previously wasn't, anywhere) so a
    /// persistently failing fetch is diagnosable going forward instead of just silently serving
    /// old data forever.
    /// </summary>
    public async Task<T?> GetJsonCachedAsync<T>(string url) where T : class
    {
        var cacheKey = "studylife-cache-v2:" + url;
        try
        {
            var result = await _http.GetFromJsonAsync<T>(url);
            if (result != null) await StoreReadCacheAsync(cacheKey, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetJsonCachedAsync failed for {Url}, falling back to the local cache", url);
            return await LoadReadCacheAsync<T>(cacheKey);
        }
    }

    public event Action? OnChange;
    public event Action? OnSessionsChanged;
    public event Action? OnSettingsChanged;
    /// <summary>Fires on every change to the offline write queue (for a future "not synced" badge).</summary>
    public event Action? OnPendingWritesChanged;

    public int PendingWriteCount => _writeQueue.Count;

    /// <summary>Like PendingWriteCount, but loads the queue from localStorage first if needed -
    /// for the sync badge at app start (before the first poll the counter would otherwise be 0,
    /// even though entries from the last offline session are still waiting).</summary>
    public async Task<int> GetPendingWriteCountAsync()
    {
        await EnsureQueueLoadedAsync();
        return _writeQueue.Count;
    }

    public AppStateService(HttpClient http, IJSRuntime jsRuntime, ILogger<AppStateService> logger, SessionTokenStore sessionTokenStore)
    {
        _http = http;
        _jsRuntime = jsRuntime;
        _logger = logger;
        _sessionTokenStore = sessionTokenStore;
        _refreshTimer = new Timer(async _ => await PollAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private async Task PollAsync()
    {
        try
        {
            if (await _jsRuntime.InvokeAsync<bool>("isPageHidden")) return;
        }
        catch { /* JS interop unavailable, assume visible and proceed */ }

        await ReplayQueueAsync();

        var sessionsChanged = false;
        var settingsChanged = false;
        try
        {
            var dtos = await _http.GetFromJsonAsync<List<StudySessionDto>>("api/sessions");
            var sessions = dtos?.Select(FromDto).ToList() ?? new List<StudySession>();
            var hash = ComputeHash(dtos ?? new List<StudySessionDto>());
            if (hash != _sessionsHash)
            {
                _sessionsHash = hash;
                _sessionsCache = sessions;
                sessionsChanged = true;
                if (dtos != null) await StoreReadCacheAsync(ReadCacheKeySessions, dtos);
            }
        }
        catch { /* ignore poll errors */ }

        try
        {
            var dto = await _http.GetFromJsonAsync<UserSettingsDto>("api/settings");
            if (dto != null)
            {
                var incoming = FromDto(dto);
                var incomingHash = ComputeSettingsHash(incoming);
                if (incomingHash != _settingsHash)
                {
                    _settingsHash = incomingHash;
                    _settingsCache = incoming;
                    settingsChanged = true;
                    await ApplyAccentColorAsync(incoming.AccentColor);
                    await StoreReadCacheAsync(ReadCacheKeySettings, dto);
                }
            }
        }
        catch { /* ignore poll errors */ }

        if (sessionsChanged) OnSessionsChanged?.Invoke();
        if (settingsChanged) OnSettingsChanged?.Invoke();
        if (sessionsChanged || settingsChanged) NotifyStateChanged();
    }

    private static string ComputeSettingsHash(UserSettings s)
        => $"{string.Join(",", s.SelectedCourseIds.OrderBy(x => x))}|{string.Join(",", s.CompletedCourseIds.OrderBy(x => x))}|{s.Theme}|{s.AccentColor}|{s.AutoSwitchFocus}|{s.AutoSwitchMinutesBefore}|{s.MotivationalStyle}|{s.SessionReminderMinutes}|{s.CourseGoalReminderDays}|{s.InactivityThresholdDays}|{s.StudyWindowStartHour}|{s.StudyWindowEndHour}|{s.StudyDays}|{s.TargetGraduationDate:yyyy-MM-dd}|{s.CustomTimerModes}|{s.WeeklyGoalMinHours}|{s.WeeklyGoalMaxHours}|{s.MonthlyGoalMinHours}|{s.MonthlyGoalMaxHours}|{s.SessionRemindersEnabled}|{s.CourseGoalRemindersEnabled}|{s.InactivityRemindersEnabled}|{s.AchievementNotificationsEnabled}|{s.WeeklyReportEnabled}|{s.DailyMotivationEnabled}|{s.PerCourseInactivityRemindersEnabled}|{s.StreakRiskRemindersEnabled}|{s.WeeklyGoalNudgeEnabled}|{s.CourseAlmostDoneRemindersEnabled}|{s.BestStudyTimeRemindersEnabled}|{s.ComebackNudgeEnabled}|{s.NewRecordNotificationsEnabled}|{s.MonthlyReportEnabled}|{s.LastBackupDownloadAt:yyyy-MM-dd}|{s.ActiveStudyProgramId}|{s.ProgressShareEnabled}|{s.ProgressShareToken}";

    // Serializes the full DTOs (ordered by Id) so EVERY field change is detected - a
    // hand-picked field subset here silently breaks cross-device sync for the omitted fields.
    private static string ComputeHash(List<StudySessionDto> sessions)
        => JsonSerializer.Serialize(sessions.OrderBy(s => s.Id).ToList());

    public async ValueTask DisposeAsync()
    {
        if (_refreshTimer != null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
        if (_accentModule != null)
        {
            try { await _accentModule.DisposeAsync(); } catch { /* connection may already be gone (tab close) */ }
            _accentModule = null;
        }
    }

    // ── Offline write queue: load/save/enqueue/replay ───────────────────────

    /// <summary>Lazily loads the queue from localStorage exactly once (the constructor is synchronous). Corrupt JSON → empty queue.</summary>
    private async Task EnsureQueueLoadedAsync()
    {
        if (_queueLoaded) return;
        await _queueGate.WaitAsync();
        try
        {
            if (_queueLoaded) return;
            try
            {
                var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", QueueStorageKey);
                if (!string.IsNullOrWhiteSpace(json))
                    _writeQueue = JsonSerializer.Deserialize<List<QueuedWrite>>(json) ?? new List<QueuedWrite>();
            }
            catch { _writeQueue = new List<QueuedWrite>(); /* corrupt or interop not available */ }
            _queueLoaded = true;
        }
        finally { _queueGate.Release(); }
    }

    private async Task PersistQueueAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_writeQueue);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", QueueStorageKey, json);
        }
        catch { /* localStorage not available - queue lives in memory only, silent degradation */ }
    }

    private async Task EnqueueSaveSessionAsync(StudySessionDto dto)
    {
        await EnsureQueueLoadedAsync();
        // Known limitation: a session created offline has no server id, so a later
        // offline edit/delete of the same session can't be correlated (the queue replays
        // the create; another offline save of the same logical session would produce a
        // second POST → duplicate).
        // Cheap mitigation: for id 0, a new save replaces an already-queued id-0 entry
        // with the same StartTime+CourseId (same logical session, edited offline twice) -
        // last write wins.
        // The delete case needs no mitigation: DeleteSessionAsync only ever gets an
        // id > 0 (Calendar.razor only deletes when `_editSession?.Id > 0`), and sessions
        // created offline never even show up with id 0 in the UI, for lack of a
        // successful refetch.
        if (dto.Id == 0)
        {
            _writeQueue.RemoveAll(e =>
            {
                if (e.Type != TypeSaveSession) return false;
                try
                {
                    var existing = JsonSerializer.Deserialize<StudySessionDto>(e.Payload);
                    return existing != null && existing.Id == 0
                        && existing.StartTime == dto.StartTime && existing.CourseId == dto.CourseId;
                }
                catch { return false; }
            });
        }
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeSaveSession,
            Payload = JsonSerializer.Serialize(dto),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    private async Task EnqueueDeleteSessionAsync(int id)
    {
        await EnsureQueueLoadedAsync();
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeDeleteSession,
            Payload = id.ToString(),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    private async Task EnqueueDeleteSeriesAsync(string groupId, DateTime? fromDate)
    {
        await EnsureQueueLoadedAsync();
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeDeleteSeries,
            Payload = JsonSerializer.Serialize(new DeleteSeriesPayload(groupId, fromDate)),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    /// <summary>
    /// Offline case of note saving (Notes.razor/Focus.razor call this from their
    /// catch). Id &lt; 0 = temporary offline id of a newly created note (replay turns
    /// it into a POST); per note id, the last queued state wins.
    /// </summary>
    public async Task EnqueueNoteSaveAsync(NoteDto dto)
    {
        await EnsureQueueLoadedAsync();
        if (dto.Id != 0)
            _writeQueue.RemoveAll(e => e.Type == TypeSaveNote && QueuedNoteId(e) == dto.Id);
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeSaveNote,
            Payload = JsonSerializer.Serialize(dto),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    /// <summary>
    /// Offline case of note deletion. For a note created offline (negative
    /// temp id), only the queued save is discarded - the server doesn't know it.
    /// </summary>
    public async Task EnqueueNoteDeleteAsync(int id)
    {
        await EnsureQueueLoadedAsync();
        _writeQueue.RemoveAll(e => e.Type == TypeSaveNote && QueuedNoteId(e) == id);
        if (id > 0)
        {
            _writeQueue.Add(new QueuedWrite
            {
                Type = TypeDeleteNote,
                Payload = id.ToString(),
                QueuedAt = DateTime.UtcNow,
            });
        }
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    /// <summary>Offline case of course goal saving (Setup.razor): only the last state counts
    /// per course - the endpoint is an idempotent full upsert anyway.</summary>
    public async Task EnqueueCourseGoalSaveAsync(CourseGoalDto dto)
    {
        await EnsureQueueLoadedAsync();
        _writeQueue.RemoveAll(e =>
        {
            if (e.Type != TypeSaveCourseGoal) return false;
            try { return JsonSerializer.Deserialize<CourseGoalDto>(e.Payload)?.CourseId == dto.CourseId; }
            catch { return false; }
        });
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeSaveCourseGoal,
            Payload = JsonSerializer.Serialize(dto),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    private static int? QueuedNoteId(QueuedWrite entry)
    {
        try { return JsonSerializer.Deserialize<NoteDto>(entry.Payload)?.Id; }
        catch { return null; }
    }

    private async Task EnqueueSaveSettingsAsync(UserSettingsDto dto)
    {
        await EnsureQueueLoadedAsync();
        // Settings: only the last state counts - replace any existing entry.
        _writeQueue.RemoveAll(e => e.Type == TypeSaveSettings);
        _writeQueue.Add(new QueuedWrite
        {
            Type = TypeSaveSettings,
            Payload = JsonSerializer.Serialize(dto),
            QueuedAt = DateTime.UtcNow,
        });
        await PersistQueueAsync();
        OnPendingWritesChanged?.Invoke();
    }

    /// <summary>
    /// Replays the queue in original order. On the FIRST entry that isn't a definitive
    /// success or a definitive rejection (network error, or a 401/403/408/429/5xx response -
    /// see TryReplayEntryAsync), replay is aborted - the rest, including that entry, stays
    /// queued and is retried on the next poll.
    /// </summary>
    private async Task ReplayQueueAsync()
    {
        if (_replaying) return;
        _replaying = true;
        try
        {
            await EnsureQueueLoadedAsync();
            if (_writeQueue.Count == 0) return;
            // No session token yet (fresh login pending, or logged out) - every request would
            // just come back 401 and immediately abort the loop anyway. Skip the round trips
            // entirely and wait for a token to show up on a later poll.
            if (string.IsNullOrEmpty(_sessionTokenStore.Token)) return;

            var flushed = 0;
            while (_writeQueue.Count > 0)
            {
                if (!await TryReplayEntryAsync(_writeQueue[0])) break;
                _writeQueue.RemoveAt(0);
                flushed++;
            }

            if (flushed > 0)
            {
                await PersistQueueAsync();
                OnPendingWritesChanged?.Invoke();
                // Re-fetch the server's ground truth: invalidate the cache and reset the hash,
                // so the poll fetch immediately following is guaranteed to be recognized as a
                // change and the granular events fire.
                _sessionsCache = null;
                _sessionsHash = "";
                _settingsHash = "";
            }
        }
        finally { _replaying = false; }
    }

    /// <summary>
    /// true = entry is done: either it succeeded, or the server definitively rejected it
    /// (corrupt payload, or a 4xx like 400/404/409/422 that would keep failing forever on
    /// replay) - both cases get removed from the queue.
    /// false = entry must be retried later: a network error (still offline), or a response
    /// that means "try again", not "this write is invalid" - 401/403 (session expired/forbidden,
    /// see SessionHandler - may recover after a fresh login), 408/429 (timeout/rate limited),
    /// or any 5xx (server-side transient failure). The entry is kept and replay stops for
    /// this cycle, exactly like the network-error case, so a queue full of real offline work
    /// is never wiped out by an expired session or a flaky server.
    /// </summary>
    private async Task<bool> TryReplayEntryAsync(QueuedWrite entry)
    {
        try
        {
            switch (entry.Type)
            {
                case TypeSaveSession:
                    var sessionDto = JsonSerializer.Deserialize<StudySessionDto>(entry.Payload);
                    if (sessionDto == null) return true;
                    var sessionResponse = sessionDto.Id == 0
                        ? await _http.PostAsJsonAsync("api/sessions", sessionDto)
                        : await _http.PutAsJsonAsync($"api/sessions/{sessionDto.Id}", sessionDto);
                    return IsEntryDone(sessionResponse);
                case TypeDeleteSession:
                    var deleteSessionResponse = await _http.DeleteAsync($"api/sessions/{entry.Payload}");
                    return IsEntryDone(deleteSessionResponse);
                case TypeDeleteSeries:
                    var series = JsonSerializer.Deserialize<DeleteSeriesPayload>(entry.Payload);
                    if (series == null || string.IsNullOrEmpty(series.GroupId)) return true;
                    var query = series.FromDate.HasValue ? $"?fromDate={series.FromDate:yyyy-MM-dd}" : "";
                    var deleteSeriesResponse = await _http.DeleteAsync($"api/sessions/series/{series.GroupId}{query}");
                    return IsEntryDone(deleteSeriesResponse);
                case TypeSaveSettings:
                    var settingsDto = JsonSerializer.Deserialize<UserSettingsDto>(entry.Payload);
                    if (settingsDto == null) return true;
                    var settingsResponse = await _http.PutAsJsonAsync("api/settings", settingsDto);
                    return IsEntryDone(settingsResponse);
                case TypeSaveNote:
                    var noteDto = JsonSerializer.Deserialize<NoteDto>(entry.Payload);
                    if (noteDto == null) return true;
                    HttpResponseMessage noteResponse;
                    if (noteDto.Id <= 0)
                    {
                        noteDto.Id = 0; // strip the temporary offline id → normal create
                        noteResponse = await _http.PostAsJsonAsync("api/notes", noteDto);
                    }
                    else
                    {
                        noteResponse = await _http.PutAsJsonAsync($"api/notes/{noteDto.Id}", noteDto);
                    }
                    return IsEntryDone(noteResponse);
                case TypeDeleteNote:
                    var deleteNoteResponse = await _http.DeleteAsync($"api/notes/{entry.Payload}");
                    return IsEntryDone(deleteNoteResponse);
                case TypeSaveCourseGoal:
                    var goalDto = JsonSerializer.Deserialize<CourseGoalDto>(entry.Payload);
                    if (goalDto == null) return true;
                    var goalResponse = await _http.PutAsJsonAsync($"api/coursegoals/{goalDto.CourseId}", goalDto);
                    return IsEntryDone(goalResponse);
                default:
                    return true; // unknown type → discard
            }
        }
        catch (JsonException) { return true; } // corrupt payload → discard
        catch { return false; } // network error → still offline, stop the replay
    }

    /// <summary>
    /// Classifies a replay response: true = definitive (success or a rejection that would
    /// never succeed on retry), false = transient (session/auth or server-side) - see
    /// TryReplayEntryAsync for how each outcome is handled.
    /// </summary>
    private static bool IsEntryDone(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return true;
        var status = response.StatusCode;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) return false;
        if ((int)status >= 500) return false;
        return true; // other 4xx (400/404/409/422/...) - definitive server rejection
    }

    // ── Account info (IsOwner) ───────────────────────────────────────────────
    // Deliberately separate from Settings/ComputeSettingsHash: IsOwner isn't a user setting,
    // but a fixed fact about the account (only the first-registered user is allowed to use the
    // raw backup/restore endpoints, see BackupController.IsOwnerAsync) - Setup.razor
    // and Index.razor hide the corresponding UI for all other users.

    private bool? _isOwnerCache;

    public async Task<bool> GetIsOwnerAsync()
    {
        if (_isOwnerCache is bool cached) return cached;
        try
        {
            var dto = await _http.GetFromJsonAsync<AccountInfoDto>("api/auth/account-info");
            _isOwnerCache = dto?.IsOwner ?? false;
        }
        catch
        {
            // Conservative when in doubt: better to hide the backup/restore UI than offer
            // an action that then fails with 403.
            _isOwnerCache = false;
        }
        return _isOwnerCache.Value;
    }

    // ── Demo mode ────────────────────────────────────────────────────────────
    // Public demo instances (server started with DEMO_MODE=true, see the demo endpoints
    // in AuthController): the client shows a DEMO chip, hides passkey/push management,
    // and suppresses the backup staleness banner. Cached like _isOwnerCache - the flag
    // can't change without a server restart, one fetch per app load is enough.

    private bool? _isDemoCache;

    public async Task<bool> GetIsDemoAsync()
    {
        if (_isDemoCache is bool cached) return cached;
        try
        {
            var dto = await _http.GetFromJsonAsync<DemoInfoDto>("api/auth/demo");
            _isDemoCache = dto?.Demo ?? false;
        }
        catch
        {
            // Older server or transient error - behave like every normal deployment.
            _isDemoCache = false;
        }
        return _isDemoCache.Value;
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    // In-flight dedup: GetCoursesAsync/GetActiveGroupQuotasAsync/GetSettingsAsync itself are all
    // now started concurrently from several pages' initial load (instead of one after another) -
    // without this, a cold cache would fire off one GET api/settings per concurrent caller
    // instead of sharing the single request already on the wire. Cleared once the fetch settles
    // (success or failure) so a later cache invalidation (SaveSettingsAsync sets _settingsCache
    // directly, doesn't go through here) always starts a fresh fetch rather than replaying a
    // stale in-flight task.
    private Task<UserSettings>? _settingsFetchInFlight;

    public Task<UserSettings> GetSettingsAsync()
    {
        if (_settingsCache != null) return Task.FromResult(_settingsCache);
        return _settingsFetchInFlight ??= FetchSettingsDedupedAsync();
    }

    private async Task<UserSettings> FetchSettingsDedupedAsync()
    {
        try { return await FetchSettingsFromServerAsync(); }
        finally { _settingsFetchInFlight = null; }
    }

    public async Task<UserSettings> FetchSettingsFromServerAsync()
    {
        try
        {
            var dto = await _http.GetFromJsonAsync<UserSettingsDto>("api/settings");
            _settingsCache = FromDto(dto!);
            _settingsHash = ComputeSettingsHash(_settingsCache);
            await StoreReadCacheAsync(ReadCacheKeySettings, dto!);
        }
        catch
        {
            // Offline cold start: last successful server state instead of the defaults.
            if (_settingsCache == null
                && await LoadReadCacheAsync<UserSettingsDto>(ReadCacheKeySettings) is { } cachedDto)
            {
                _settingsCache = FromDto(cachedDto);
                _settingsHash = ComputeSettingsHash(_settingsCache);
            }
            _settingsCache ??= new UserSettings { SelectedCourseIds = new List<int> { 1, 2, 3, 4 } };
        }
        await ApplyAccentColorAsync(_settingsCache.AccentColor);
        return _settingsCache;
    }

    public async Task SaveSettingsAsync(UserSettings settings)
    {
        _settingsCache = settings;
        _settingsHash = ComputeSettingsHash(settings);
        try
        {
            await _http.PutAsJsonAsync("api/settings", ToDto(settings));
        }
        catch { await EnqueueSaveSettingsAsync(ToDto(settings)); /* offline: replay later */ }
        await ApplyAccentColorAsync(settings.AccentColor);
        OnSettingsChanged?.Invoke();
        NotifyStateChanged();
    }

    private IJSObjectReference? _accentModule;

    /// <summary>
    /// Sets the curated accent color as a data attribute on &lt;html&gt; (wwwroot/js/accent.js,
    /// mirrors the existing theme mechanism exactly). Its own JS module via dynamic
    /// import() instead of a global index.html script, because index.html is intentionally left
    /// untouched - analogous to the already-existing Focus.razor.js/SetupBackupCard.razor.js
    /// pattern. Wired centrally here instead of in MainLayout/Setup so it automatically applies
    /// on EVERY load/save AND on the 30s cross-client poll (the theme, by contrast, is only
    /// applied on the initial MainLayout mount resp. explicitly in the Setup theme switch).
    /// </summary>
    private async Task ApplyAccentColorAsync(string accentColor)
    {
        try
        {
            _accentModule ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/accent.js");
            await _accentModule.InvokeVoidAsync("applyAccent", accentColor);
        }
        catch { /* JS interop/module not available (e.g. prerendering) - CSS default applies then */ }
    }

    // ── Courses ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Course list of the ACTIVE study program. Cached program-aware: if
    /// UserSettings.ActiveStudyProgramId changes (locally or via a poll from another device),
    /// it's automatically reloaded on the next call. For custom study
    /// programs the program id is sent as a query parameter - purely a
    /// URL cache buster against /api/courses's hourly browser max-age.
    /// </summary>
    public async Task<List<CourseDto>> GetCoursesAsync()
    {
        var settings = await GetSettingsAsync();
        var programId = settings.ActiveStudyProgramId;
        if (_coursesCache != null && _coursesCacheProgramId == programId) return _coursesCache;
        try
        {
            var url = programId.HasValue ? $"api/courses?program={programId.Value}" : "api/courses";
            var courses = await _http.GetFromJsonAsync<List<CourseDto>>(url);
            _coursesCache = courses ?? new List<CourseDto>();
            if (courses != null)
                await StoreReadCacheAsync(ReadCacheKeyCourses, new CachedCourses(programId, courses));
        }
        catch
        {
            // Offline cold start: only use it if the cache belongs to the active study program.
            var cached = await LoadReadCacheAsync<CachedCourses>(ReadCacheKeyCourses);
            _coursesCache = cached is not null && cached.ProgramId == programId
                ? cached.Courses
                : new List<CourseDto>();
        }
        _coursesCacheProgramId = programId;
        return _coursesCache;
    }

    /// <summary>
    /// ECTS quotas per elective group of the ACTIVE study program, for the
    /// program-aware CourseCatalog.CalcTotalEcts/CalcEctsEarned overloads.
    /// Built-in study program: the static CourseCatalog.GroupEctsQuotas without any
    /// network access; custom program: fetched once from GET /api/studyprograms/{id}
    /// and cached per program. Fetch failure ⇒ empty dictionary (uncached),
    /// which makes groups count as defensively full instead of not at all.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetActiveGroupQuotasAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings.ActiveStudyProgramId is not int programId)
            return CourseCatalog.GroupEctsQuotas;
        if (_groupQuotasCache != null && _groupQuotasCacheProgramId == programId) return _groupQuotasCache;
        try
        {
            var detail = await _http.GetFromJsonAsync<StudyProgramDetailDto>($"api/studyprograms/{programId}");
            if (detail == null) return new Dictionary<string, int>();
            _groupQuotasCache = detail.GroupEctsQuotas;
            _groupQuotasCacheProgramId = programId;
            return _groupQuotasCache;
        }
        catch
        {
            return new Dictionary<string, int>();
        }
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public async Task<List<StudySession>> GetSessionsAsync()
    {
        if (_sessionsCache != null) return _sessionsCache;
        try
        {
            var dtos = await _http.GetFromJsonAsync<List<StudySessionDto>>("api/sessions");
            _sessionsCache = dtos?.Select(FromDto).ToList() ?? new List<StudySession>();
            _sessionsHash = ComputeHash(dtos ?? new List<StudySessionDto>());
            if (dtos != null) await StoreReadCacheAsync(ReadCacheKeySessions, dtos);
        }
        catch
        {
            // Offline cold start: last successful server state instead of an empty list.
            var cached = await LoadReadCacheAsync<List<StudySessionDto>>(ReadCacheKeySessions);
            _sessionsCache = cached?.Select(FromDto).ToList() ?? new List<StudySession>();
            _sessionsHash = ComputeHash(cached ?? new List<StudySessionDto>());
        }
        return _sessionsCache;
    }

    public async Task SaveSessionAsync(StudySession session)
    {
        try
        {
            if (session.Id == 0)
            {
                var response = await _http.PostAsJsonAsync("api/sessions", ToDto(session));
                if (response.IsSuccessStatusCode)
                {
                    var dto = await response.Content.ReadFromJsonAsync<StudySessionDto>();
                    if (dto != null) session.Id = dto.Id;
                }
            }
            else
            {
                await _http.PutAsJsonAsync($"api/sessions/{session.Id}", ToDto(session));
            }
        }
        catch { await EnqueueSaveSessionAsync(ToDto(session)); /* offline: replay later */ }
        _sessionsCache = null;
        OnSessionsChanged?.Invoke();
        NotifyStateChanged();
    }

    public async Task DeleteSessionAsync(int id)
    {
        try { await _http.DeleteAsync($"api/sessions/{id}"); }
        catch { await EnqueueDeleteSessionAsync(id); /* offline: replay later */ }
        _sessionsCache = null;
        OnSessionsChanged?.Invoke();
        NotifyStateChanged();
    }

    public async Task DeleteSeriesAsync(string groupId, DateTime? fromDate)
    {
        try
        {
            var query = fromDate.HasValue ? $"?fromDate={fromDate:yyyy-MM-dd}" : "";
            await _http.DeleteAsync($"api/sessions/series/{groupId}{query}");
        }
        catch { await EnqueueDeleteSeriesAsync(groupId, fromDate); /* offline: replay later */ }
        _sessionsCache = null;
        OnSessionsChanged?.Invoke();
        NotifyStateChanged();
    }

    public async Task<StudySession?> GetActiveSessionAsync()
    {
        var sessions = await GetSessionsAsync();
        var now = DateTime.Now;
        return sessions.FirstOrDefault(s => !s.IsCompleted && s.StartTime <= now && s.EndTime >= now);
    }

    public async Task<StudySession?> GetUpcomingSessionAsync()
    {
        var sessions = await GetSessionsAsync();
        var now = DateTime.Now;
        return sessions
            .Where(s => !s.IsCompleted && s.StartTime > now)
            .OrderBy(s => s.StartTime)
            .FirstOrDefault();
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static StudySession FromDto(StudySessionDto d) => new()
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

    private static StudySessionDto ToDto(StudySession s) => new()
    {
        Id = s.Id,
        CourseId = s.CourseId,
        CourseName = s.CourseName,
        CourseColor = s.CourseColor,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        Topic = s.Topic,
        Notes = s.Notes,
        IsCompleted = s.IsCompleted,
        TimerModeId = s.TimerModeId,
        RecurrenceGroupId = s.RecurrenceGroupId,
    };

    private static UserSettings FromDto(UserSettingsDto d) => new()
    {
        SelectedCourseIds = d.SelectedCourseIds,
        CompletedCourseIds = d.CompletedCourseIds,
        Theme = d.Theme,
        AccentColor = d.AccentColor,
        AutoSwitchFocus = d.AutoSwitchFocus,
        AutoSwitchMinutesBefore = d.AutoSwitchMinutesBefore,
        MotivationalStyle = d.MotivationalStyle,
        SessionReminderMinutes = d.SessionReminderMinutes,
        CourseGoalReminderDays = d.CourseGoalReminderDays,
        InactivityThresholdDays = d.InactivityThresholdDays,
        StudyWindowStartHour = d.StudyWindowStartHour,
        StudyWindowEndHour = d.StudyWindowEndHour,
        StudyDays = d.StudyDays,
        TargetGraduationDate = d.TargetGraduationDate,
        CustomTimerModes = d.CustomTimerModes,
        WeeklyGoalMinHours = d.WeeklyGoalMinHours,
        WeeklyGoalMaxHours = d.WeeklyGoalMaxHours,
        MonthlyGoalMinHours = d.MonthlyGoalMinHours,
        MonthlyGoalMaxHours = d.MonthlyGoalMaxHours,
        SessionRemindersEnabled = d.SessionRemindersEnabled,
        CourseGoalRemindersEnabled = d.CourseGoalRemindersEnabled,
        InactivityRemindersEnabled = d.InactivityRemindersEnabled,
        AchievementNotificationsEnabled = d.AchievementNotificationsEnabled,
        WeeklyReportEnabled = d.WeeklyReportEnabled,
        DailyMotivationEnabled = d.DailyMotivationEnabled,
        PerCourseInactivityRemindersEnabled = d.PerCourseInactivityRemindersEnabled,
        StreakRiskRemindersEnabled = d.StreakRiskRemindersEnabled,
        WeeklyGoalNudgeEnabled = d.WeeklyGoalNudgeEnabled,
        CourseAlmostDoneRemindersEnabled = d.CourseAlmostDoneRemindersEnabled,
        BestStudyTimeRemindersEnabled = d.BestStudyTimeRemindersEnabled,
        ComebackNudgeEnabled = d.ComebackNudgeEnabled,
        NewRecordNotificationsEnabled = d.NewRecordNotificationsEnabled,
        MonthlyReportEnabled = d.MonthlyReportEnabled,
        LastBackupDownloadAt = d.LastBackupDownloadAt,
        ActiveStudyProgramId = d.ActiveStudyProgramId,
        ProgressShareEnabled = d.ProgressShareEnabled,
        ProgressShareToken = d.ProgressShareToken,
    };

    private static UserSettingsDto ToDto(UserSettings s) => new()
    {
        SelectedCourseIds = s.SelectedCourseIds,
        CompletedCourseIds = s.CompletedCourseIds,
        Theme = s.Theme,
        AccentColor = s.AccentColor,
        AutoSwitchFocus = s.AutoSwitchFocus,
        AutoSwitchMinutesBefore = s.AutoSwitchMinutesBefore,
        MotivationalStyle = s.MotivationalStyle,
        SessionReminderMinutes = s.SessionReminderMinutes,
        CourseGoalReminderDays = s.CourseGoalReminderDays,
        InactivityThresholdDays = s.InactivityThresholdDays,
        StudyWindowStartHour = s.StudyWindowStartHour,
        StudyWindowEndHour = s.StudyWindowEndHour,
        StudyDays = s.StudyDays,
        TargetGraduationDate = s.TargetGraduationDate,
        CustomTimerModes = s.CustomTimerModes,
        WeeklyGoalMinHours = s.WeeklyGoalMinHours,
        WeeklyGoalMaxHours = s.WeeklyGoalMaxHours,
        MonthlyGoalMinHours = s.MonthlyGoalMinHours,
        MonthlyGoalMaxHours = s.MonthlyGoalMaxHours,
        SessionRemindersEnabled = s.SessionRemindersEnabled,
        CourseGoalRemindersEnabled = s.CourseGoalRemindersEnabled,
        InactivityRemindersEnabled = s.InactivityRemindersEnabled,
        AchievementNotificationsEnabled = s.AchievementNotificationsEnabled,
        WeeklyReportEnabled = s.WeeklyReportEnabled,
        DailyMotivationEnabled = s.DailyMotivationEnabled,
        PerCourseInactivityRemindersEnabled = s.PerCourseInactivityRemindersEnabled,
        StreakRiskRemindersEnabled = s.StreakRiskRemindersEnabled,
        WeeklyGoalNudgeEnabled = s.WeeklyGoalNudgeEnabled,
        CourseAlmostDoneRemindersEnabled = s.CourseAlmostDoneRemindersEnabled,
        BestStudyTimeRemindersEnabled = s.BestStudyTimeRemindersEnabled,
        ComebackNudgeEnabled = s.ComebackNudgeEnabled,
        NewRecordNotificationsEnabled = s.NewRecordNotificationsEnabled,
        MonthlyReportEnabled = s.MonthlyReportEnabled,
        LastBackupDownloadAt = s.LastBackupDownloadAt,
        ActiveStudyProgramId = s.ActiveStudyProgramId,
        ProgressShareEnabled = s.ProgressShareEnabled,
        ProgressShareToken = s.ProgressShareToken,
    };

    private void NotifyStateChanged() => OnChange?.Invoke();
}
