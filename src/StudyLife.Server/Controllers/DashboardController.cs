using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// GET api/dashboard/summary - server-side twin of Index.razor.cs's LoadDataAsync: assembles
/// exactly the DashboardSummaryInput the client would build from its own nine fetches (api/
/// settings, api/courses, api/sessions, api/sessions/history?days=400&amp;onlyCompleted=false,
/// api/sessions/history?days=3650, api/coursegoals, api/studyprograms, api/notes, plus the
/// IsOwner/IsDemo/RawBackupSupported facts), then runs the shared DashboardSummaryBuilder - so a
/// thin/native client can render the whole dashboard from one round trip instead of nine. Every
/// number is still computed by the exact same StudyLife.Shared code the web client runs
/// in-process; this controller's job is only resolving the same DB reads those nine endpoints
/// would have produced for THIS user.
///
/// SessionOnly (like TelemetryController), not the ApiAccess default MetricsController uses: this
/// is a browser/native-client-only convenience endpoint, never part of the add-on API-key surface
/// - no slot in ApiKeyScopes lists it, and SessionOnly rejects any bare API key outright before
/// scoping even comes into play.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class DashboardController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly SessionHistoryCacheVersion _historyVersion;
    private readonly SettingsCacheVersion _settingsVersion;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IProgrammeScopeResolver _scope;
    private readonly IOwnershipService _ownership;
    private readonly IConfiguration _config;
    private readonly bool _rawBackupSupported;

    /// <summary>Same upper bound as MetricsController's summary cache - the version-keyed key
    /// already changes on every session/settings/goal write and the notes token on every note
    /// write, so this only covers inputs that bump no counter (the wall clock moving within the
    /// same minute).</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Sanity window for the client-supplied `now`: a legitimate client's local clock is
    /// never off from the server's by more than this. Without this clamp, `now` (part of the
    /// cache key) would let a caller mint an unbounded number of distinct cache entries.</summary>
    private static readonly TimeSpan NowClampWindow = TimeSpan.FromHours(36);

    public DashboardController(StudyLifeDb db, IDistributedCache cache, SessionHistoryCacheVersion historyVersion,
        SettingsCacheVersion settingsVersion, ICurrentUserAccessor currentUser, IProgrammeScopeResolver scope,
        IOwnershipService ownership, IConfiguration config,
        DatabaseBackupService? backupService = null, DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        _cache = cache;
        _historyVersion = historyVersion;
        _settingsVersion = settingsVersion;
        _currentUser = currentUser;
        _scope = scope;
        _ownership = ownership;
        _config = config;
        // Same derivation as SystemController.GetCapabilities/BackupController.IsRawBackupAvailable:
        // both services are only registered in SQLite mode (Program.cs), and never on a demo
        // instance (DemoModeGuard.IsEnabled, not a bare DEMO_MODE check, to agree with Program.cs
        // about whether the write-block middleware is actually registered).
        _rawBackupSupported = backupService is not null && restoreService is not null && !DemoModeGuard.IsEnabled(config);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> GetSummary([FromQuery] DateTime? now)
    {
        if (now is not { } clientNow) return BadRequest("now is required.");
        if (Math.Abs((clientNow - DateTime.Now).TotalHours) > NowClampWindow.TotalHours)
            return BadRequest("now is too far from the server's clock.");

        // Programme resolution runs BEFORE the cache (same reasoning as MetricsController): a 404
        // must never be cached, and the factory below can then rely on a resolved programme.
        var activeProgramId = await _db.Settings.AsNoTracking().Select(s => (int?)s.ActiveStudyProgramId).FirstOrDefaultAsync();
        var scope = await _scope.ResolveAsync(null, activeProgramId);
        if (scope == null) return NotFound();

        var userId = _currentUser.AuthUserId;
        // Cheap marker for "did the note set change" - Notes has no version counter of its own
        // (unlike Sessions/Settings), so count + the newest UpdatedAt stand in for one without a
        // dedicated Redis counter for a single field. Read on every call (like the two version
        // counters below), not just on a cache miss, since it is itself part of the cache key.
        var notesToken = await LoadNotesTokenAsync();

        var cacheKey = $"dashboard:summary:{userId}:{clientNow:yyyyMMddHHmm}"
            + $":{await _historyVersion.GetAsync(userId)}:{await _settingsVersion.GetAsync(userId)}:{notesToken}";
        return await _cache.GetOrSetAsync(this, cacheKey, CacheTtl, () => ComputeAsync(clientNow, scope));
    }

    private async Task<string> LoadNotesTokenAsync()
    {
        var stats = await _db.Notes.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxUpdatedAt = (DateTime?)g.Max(n => n.UpdatedAt) })
            .FirstOrDefaultAsync();
        return stats == null ? "0" : $"{stats.Count}:{stats.MaxUpdatedAt:O}";
    }

    private async Task<DashboardSummaryDto> ComputeAsync(DateTime now, ProgrammeScope scope)
    {
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new UserSettingsEntity();
        var settings = SettingsController.ToDto(settingsEntity);

        // Sessions/History/HeavyHistory are all the same underlying table, filtered only by a
        // date window relative to the current moment - one fetch here instead of three separate
        // round trips (GET api/sessions / api/sessions/history?days=400 / ?days=3650), materialized
        // then mapped in-memory, same shape as MetricsController's own session read.
        var sessionEntities = await _db.Sessions.AsNoTracking().ToListAsync();
        var allSessions = sessionEntities.Select(SessionsController.ToDto).ToList();

        // The window boundary every history query compares against must be DateTime.Now, not
        // UtcNow - see SessionsController.GetHistory's own "audit finding Z1" comment (StartTime/
        // EndTime are naive local). One reading shared by both windows below, instead of the real
        // endpoints' two independent DateTime.Now reads - only tightens, never loosens, parity.
        var serverNow = DateTime.Now;

        // GET /api/sessions/history?days=400&onlyCompleted=false: no onlyCompleted filter at all.
        var historyFrom = serverNow.AddDays(-DashboardSummaryBuilder.HistoryDays);
        var history = allSessions.Where(s => s.StartTime >= historyFrom).ToList();

        // GET /api/sessions/history?days=3650 (onlyCompleted defaults to true): "studied" means
        // timer-completed OR the scheduled end has already passed, same as GetHistory itself.
        var heavyFrom = serverNow.AddDays(-DashboardSummaryBuilder.AchievementHistoryDays);
        var heavyHistory = allSessions
            .Where(s => s.StartTime >= heavyFrom && (s.IsCompleted || s.EndTime <= serverNow))
            .ToList();

        var goalEntities = await _db.CourseGoals.AsNoTracking().ToListAsync();
        var goals = goalEntities.Select(CourseGoalsController.ToDto).ToList();

        var noteEntities = await _db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).ToListAsync();
        var notes = noteEntities.Select(NotesController.ToDto).ToList();

        var studyPrograms = await StudyProgramsController.LoadSummariesAsync(_db);

        var input = new DashboardSummaryInput
        {
            Settings = settings,
            AllCourses = scope.Catalog,
            Sessions = allSessions,
            History = history,
            HeavyHistory = heavyHistory,
            Goals = goals,
            GroupQuotas = scope.GroupQuotas,
            StudyPrograms = studyPrograms,
            Notes = notes,
            IsOwner = await _ownership.IsOwnerAsync(_currentUser.AuthUserId),
            IsDemo = DemoModeGuard.IsEnabled(_config),
            RawBackupSupported = _rawBackupSupported,
            Now = now,
        };
        return DashboardSummaryBuilder.Build(input);
    }
}
