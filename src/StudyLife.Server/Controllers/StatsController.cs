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
/// GET api/stats/summary - server-side twin of Stats.razor.cs's LoadDataAsync/
/// LoadProgramCatalogsAsync: assembles exactly the StatsSummaryInput the client would build from
/// its own fetches (api/settings, api/courses, api/sessions, api/sessions/history?days=371,
/// api/sessions/history?days=3650, api/coursegoals, active-programme group quotas,
/// api/studyprograms, and - for every programme once there are at least two - its own
/// api/courses?program={id} plus api/studyprograms/{id}, api/notes), then runs the shared
/// StatsSummaryBuilder - same reasoning and same auth/caching pattern as DashboardController.
/// </summary>
[ApiController]
[Route("api/stats")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class StatsController : ControllerBase
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

    public StatsController(StudyLifeDb db, IDistributedCache cache, SessionHistoryCacheVersion historyVersion,
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
    public async Task<ActionResult<StatsSummaryDto>> GetSummary([FromQuery] DateTime? now)
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

        var cacheKey = $"stats:summary:{userId}:{clientNow:yyyyMMddHHmm}"
            + $":{await _historyVersion.GetAsync(userId)}:{await _settingsVersion.GetAsync(userId)}:{notesToken}";
        return await _cache.GetOrSetAsync(this, cacheKey, CacheTtl, () => ComputeAsync(clientNow, scope));
    }

    private async Task<StatsSummaryDto> ComputeAsync(DateTime now, ProgrammeScope scope)
    {
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new UserSettingsEntity();
        var settings = SettingsController.ToDto(settingsEntity);

        var allSessions = await _loader.LoadAllSessionsAsync();
        // GET /api/sessions/history?days=371 and ?days=3650 - both studied-only (onlyCompleted
        // defaults to true), same as Stats.razor.cs's two history fetches.
        var history = SummaryInputLoader.SliceHistory(allSessions, now, StatsSummaryBuilder.HistoryDays);
        var heavyHistory = SummaryInputLoader.SliceHistory(allSessions, now, StatsSummaryBuilder.AllTimeHistoryDays);

        var goalEntities = await _db.CourseGoals.AsNoTracking().ToListAsync();
        var goals = goalEntities.Select(CourseGoalsController.ToDto).ToList();

        var noteEntities = await _db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).ToListAsync();
        var notes = noteEntities.Select(NotesController.ToDto).ToList();

        var studyPrograms = await StudyProgramsController.LoadSummariesAsync(_db);
        var programCatalogs = await LoadProgramCatalogsAsync(studyPrograms);

        var input = new StatsSummaryInput
        {
            Settings = settings,
            AllCourses = scope.Catalog,
            Sessions = allSessions,
            History = history,
            HeavyHistory = heavyHistory,
            Goals = goals,
            GroupQuotas = scope.GroupQuotas,
            StudyPrograms = studyPrograms,
            ProgramCatalogs = programCatalogs,
            Notes = notes,
            Now = now,
        };
        return StatsSummaryBuilder.Build(input);
    }

    /// <summary>
    /// Server-side equivalent of Stats.Programs.razor.cs's LoadProgramCatalogsAsync: one catalog
    /// entry per programme, but only once there are at least two (with a single programme the
    /// comparison card stays hidden and the client never fires the per-programme fan-out either -
    /// same gate here so a lone custom programme costs no extra queries). The built-in programme
    /// (Id null) has no DB row - courses come from the static catalog and its quotas are ignored
    /// by the builder (CourseCatalog.GroupEctsQuotas is used directly for it), matching the client
    /// never fetching a detail row for it either.
    /// </summary>
    private async Task<List<StatsProgramCatalogDto>> LoadProgramCatalogsAsync(List<StudyProgramSummaryDto> studyPrograms)
    {
        if (studyPrograms.Count < 2) return new List<StatsProgramCatalogDto>();

        var catalogs = new List<StatsProgramCatalogDto>();
        foreach (var program in studyPrograms)
        {
            if (program.Id is int programId)
            {
                catalogs.Add(new StatsProgramCatalogDto
                {
                    ProgramId = programId,
                    Courses = await StudyProgramCatalog.LoadCoursesAsync(_db, programId),
                    GroupQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(_db, programId),
                });
            }
            else
            {
                catalogs.Add(new StatsProgramCatalogDto
                {
                    ProgramId = null,
                    Courses = CourseCatalog.AppliedAICourses,
                    GroupQuotas = new Dictionary<string, int>(),
                });
            }
        }
        return catalogs;
    }
}
