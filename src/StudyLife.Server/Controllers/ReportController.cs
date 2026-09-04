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
/// GET api/report/summary - server-side twin of Report.razor.cs's OnTextLoadedAsync: assembles
/// exactly the ReportSummaryInput the client would build from its own five fetches (api/settings,
/// api/courses, api/coursegoals, api/sessions/history?days=3650, api/studyprograms, plus the
/// active-programme's group quotas), then runs the shared ReportSummaryBuilder - same reasoning
/// and same auth/caching pattern as DashboardController. No notes token in the cache key: the
/// printable report never reads notes at all.
/// </summary>
[ApiController]
[Route("api/report")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class ReportController : ControllerBase
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

    public ReportController(StudyLifeDb db, IDistributedCache cache, SessionHistoryCacheVersion historyVersion,
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
    public async Task<ActionResult<ReportSummaryDto>> GetSummary([FromQuery] DateTime? now)
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
        var cacheKey = $"report:summary:{userId}:{clientNow:yyyyMMddHHmm}"
            + $":{await _historyVersion.GetAsync(userId)}:{await _settingsVersion.GetAsync(userId)}";
        return await _cache.GetOrSetAsync(this, cacheKey, CacheTtl, () => ComputeAsync(clientNow, scope));
    }

    private async Task<ReportSummaryDto> ComputeAsync(DateTime now, ProgrammeScope scope)
    {
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new UserSettingsEntity();
        var settings = SettingsController.ToDto(settingsEntity);

        var allSessions = await _loader.LoadAllSessionsAsync();
        // GET /api/sessions/history?days=3650 (onlyCompleted defaults to true, i.e. studied-only) -
        // a study record must show the ENTIRE study time to date, same as Report.razor.cs's fetch.
        var history = SummaryInputLoader.SliceHistory(allSessions, now, ReportSummaryBuilder.HistoryDays);

        var goalEntities = await _db.CourseGoals.AsNoTracking().ToListAsync();
        var goals = goalEntities.Select(CourseGoalsController.ToDto).ToList();

        var studyPrograms = await StudyProgramsController.LoadSummariesAsync(_db);

        var input = new ReportSummaryInput
        {
            Settings = settings,
            AllCourses = scope.Catalog,
            Goals = goals,
            History = history,
            GroupQuotas = scope.GroupQuotas,
            StudyPrograms = studyPrograms,
            Now = now,
        };
        return ReportSummaryBuilder.Build(input);
    }
}
