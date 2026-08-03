using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/courses")]
public class CoursesController : ControllerBase
{
    private const string CacheKey = "courses:all";

    private readonly IDistributedCache _cache;
    private readonly StudyLifeDb _db;

    public CoursesController(IDistributedCache cache, StudyLifeDb db)
    {
        _cache = cache;
        _db = db;
    }

    /// <param name="program">
    /// Optional explicit study-program id (0 = built-in catalog). Serves the client mainly
    /// as a URL cache-buster: the response carries a browser-side max-age, and without a
    /// program-specific URL, switching study programs would keep seeing the cached course
    /// list of the previous program for up to an hour. Without a parameter (existing clients,
    /// Home Assistant) the active study program is resolved from settings.
    /// </param>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CourseDto>>> GetAll([FromQuery] int? program = null)
    {
        int? programId;
        if (program.HasValue)
        {
            programId = program.Value == 0 ? null : program.Value;
        }
        else
        {
            var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync();
            programId = settings?.ActiveStudyProgramId;
        }

        // Unknown id (e.g. stale client state) → defensively fall back to the built-in catalog.
        if (programId.HasValue && !await _db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == programId.Value))
            programId = null;

        if (programId == null)
        {
            // Built-in catalog: exactly the previous behavior. It only changes via a code
            // deploy, so the hourly TTL is just a defensive bound. The only endpoint with a
            // browser-side max-age - other data needs to revalidate (CacheHelper).
            return await _cache.GetOrSetAsync<IEnumerable<CourseDto>>(this, CacheKey, TimeSpan.FromHours(1),
                () => Task.FromResult<IEnumerable<CourseDto>>(CourseCatalog.AppliedAICourses),
                clientMaxAge: TimeSpan.FromHours(1));
        }

        // Custom study program: immutable after creation (no edit endpoint), so the same
        // hourly max-age can be set here - the program-specific URL (?program=...) keeps the
        // browser caches of the different programs apart.
        var id = programId.Value;
        return await _cache.GetOrSetAsync<IEnumerable<CourseDto>>(this, $"courses:program:{id}", TimeSpan.FromHours(1),
            async () => await StudyProgramCatalog.LoadCoursesAsync(_db, id),
            clientMaxAge: TimeSpan.FromHours(1));
    }
}
