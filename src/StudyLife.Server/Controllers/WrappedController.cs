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
/// GET api/wrapped/summary - server-side twin of Wrapped.razor.cs's OnTextLoadedAsync: assembles
/// exactly the WrappedSummaryInput the client would build from its own six fetches (api/settings,
/// api/courses, api/sessions/history?days=365, api/sessions/history?days=3650, api/notes,
/// api/studyprograms, plus the active-programme's group quotas), then runs the shared
/// WrappedSummaryBuilder - same reasoning and same auth/caching pattern as DashboardController.
/// </summary>
[ApiController]
[Route("api/wrapped")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class WrappedController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly SessionHistoryCacheVersion _historyVersion;
    private readonly SettingsCacheVersion _settingsVersion;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IProgrammeScopeResolver _scope;
    private readonly SummaryInputLoader _loader;

    /// <summary>Same TTL/reasoning as DashboardController.CacheTtl.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Same clamp/reasoning as DashboardController.NowClampWindow.</summary>
    private static readonly TimeSpan NowClampWindow = TimeSpan.FromHours(36);

    public WrappedController(StudyLifeDb db, IDistributedCache cache, SessionHistoryCacheVersion historyVersion,
        SettingsCacheVersion settingsVersion, ICurrentUserAccessor currentUser, IProgrammeScopeResolver scope,
        SummaryInputLoader loader)
    {
        _db = db;
        _cache = cache;
        _historyVersion = historyVersion;
        _settingsVersion = settingsVersion;
        _currentUser = currentUser;
        _scope = scope;
        _loader = loader;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<WrappedSummaryDto>> GetSummary([FromQuery] DateTime? now)
    {
        if (now is not { } clientNow) return BadRequest("now is required.");
        if (Math.Abs((clientNow - DateTime.Now).TotalHours) > NowClampWindow.TotalHours)
            return BadRequest("now is too far from the server's clock.");

        // Programme resolution runs BEFORE the cache (same reasoning as DashboardController): a
        // 404 must never be cached, and the factory below can then rely on a resolved programme.
        var activeProgramId = await _db.Settings.AsNoTracking().Select(s => (int?)s.ActiveStudyProgramId).FirstOrDefaultAsync();
        var scope = await _scope.ResolveAsync(null, activeProgramId);
        if (scope == null) return NotFound();

        var userId = _currentUser.AuthUserId;
        var notesToken = await _loader.LoadNotesTokenAsync();

        var cacheKey = $"wrapped:summary:{userId}:{clientNow:yyyyMMddHHmm}"
            + $":{await _historyVersion.GetAsync(userId)}:{await _settingsVersion.GetAsync(userId)}:{notesToken}";
        return await _cache.GetOrSetAsync(this, cacheKey, CacheTtl, () => ComputeAsync(clientNow, scope));
    }

    private async Task<WrappedSummaryDto> ComputeAsync(DateTime now, ProgrammeScope scope)
    {
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new UserSettingsEntity();
        var settings = SettingsController.ToDto(settingsEntity);

        var allSessions = await _loader.LoadAllSessionsAsync();
        // GET /api/sessions/history?days=365 (recap window) and ?days=3650 (achievements) - both
        // studied-only (onlyCompleted defaults to true), same as Wrapped.razor.cs's two fetches.
        var periodHistory = SummaryInputLoader.SliceHistory(allSessions, now, WrappedSummaryBuilder.PeriodHistoryDays);
        var allTimeHistory = SummaryInputLoader.SliceHistory(allSessions, now, WrappedSummaryBuilder.AllTimeHistoryDays);

        var noteEntities = await _db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).ToListAsync();
        var notes = noteEntities.Select(NotesController.ToDto).ToList();

        var studyPrograms = await StudyProgramsController.LoadSummariesAsync(_db);

        var input = new WrappedSummaryInput
        {
            Settings = settings,
            AllCourses = scope.Catalog,
            PeriodHistory = periodHistory,
            AllTimeHistory = allTimeHistory,
            GroupQuotas = scope.GroupQuotas,
            StudyPrograms = studyPrograms,
            Notes = notes,
            Now = now,
        };
        return WrappedSummaryBuilder.Build(input);
    }
}
