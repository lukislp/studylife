using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly IDistributedCache _cache;
    private readonly ISessionService _sessions;

    public SessionsController(IDistributedCache cache, ISessionService sessions)
    {
        _cache = cache;
        _sessions = sessions;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StudySessionDto>>> GetAll()
    {
        // The cache key already changes on every write (per-user version counter), so the TTL is
        // only a memory bound, not a freshness mechanism - see SessionService.CacheTtl. The
        // ETag/Cache-Control wrapper stays here rather than in the service: it needs the request
        // and response of THIS request (see CacheHelper), which is HTTP, not domain.
        var cacheKey = await _sessions.AllCacheKeyAsync();
        return await _cache.GetOrSetAsync<IEnumerable<StudySessionDto>>(this, cacheKey, SessionService.CacheTtl,
            async () => await _sessions.LoadAllAsync());
    }

    [HttpPost]
    public async Task<ActionResult<StudySessionDto>> Create(StudySessionDto dto) =>
        (await _sessions.CreateAsync(dto)).ToActionResult(this);

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, StudySessionDto dto) =>
        (await _sessions.UpdateAsync(id, dto)).ToOkResult(this);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        (await _sessions.DeleteAsync(id)).ToNoContentResult(this);

    [HttpDelete("series/{groupId}")]
    public async Task<IActionResult> DeleteSeries(string groupId, [FromQuery] DateTime? fromDate)
    {
        await _sessions.DeleteSeriesAsync(groupId, fromDate);
        return NoContent();
    }

    /// <summary>
    /// Long-term history (default: 1 year, completed sessions only) for analytics charts
    /// and dashboard calculations that need to look back further than ±7/90 days (streak,
    /// monthly quota, weekly trend) - <see cref="GetAll"/> deliberately only returns this narrow
    /// window for calendar/dashboard "today/upcoming" displays. <paramref name="onlyCompleted"/>=false
    /// also returns non-completed sessions (for calculations that count "all sessions in the
    /// period" instead of only completed ones, e.g. weekly hours/trend, analogous to the
    /// "This week" tile).
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<IEnumerable<StudySessionDto>>> GetHistory([FromQuery] int days = 365, [FromQuery] bool onlyCompleted = true)
    {
        // Clamped before the key is built, so the cache entry is keyed by the EFFECTIVE window
        // rather than by whatever the query string said - see SessionService.ClampHistoryDays.
        days = SessionService.ClampHistoryDays(days);
        var cacheKey = await _sessions.HistoryCacheKeyAsync(days, onlyCompleted);
        return await _cache.GetOrSetAsync<IEnumerable<StudySessionDto>>(this, cacheKey, SessionService.CacheTtl,
            async () => await _sessions.LoadHistoryAsync(days, onlyCompleted));
    }

    /// <summary>
    /// Subscribable iCalendar feed for Google/Apple Calendar & co. - deliberately still
    /// windowed to -7/+90 days (unlike <see cref="GetAll"/>, which now returns full history for
    /// the app's own calendar view): an external calendar app resyncing this feed doesn't need
    /// years of past sessions, and keeping it bounded avoids ballooning the feed as history
    /// grows. Times are written as "floating" (no TZID, no Z suffix) because StartTime/EndTime
    /// are naive local time - see docs/ARCHITECTURE.md for the app's timezone handling.
    /// </summary>
    [HttpGet("ics")]
    public async Task<IActionResult> GetIcs()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(await _sessions.BuildIcsAsync());
        return File(bytes, "text/calendar; charset=utf-8");
    }

    /// <summary>
    /// Accepts an uploaded .ics file and returns the parsed VEVENTs for review in the client -
    /// deliberately does NOT create any sessions yet, because the course (CourseId/name)
    /// cannot be inferred from a foreign .ics and the user has to choose it per appointment
    /// in the client (see Calendar.ImportIcs.razor.cs). The actual creation then happens via
    /// the normal POST /api/sessions, once per appointment confirmed by the user - no dedicated
    /// bulk-insert endpoint, to avoid maintaining duplicate validation/cache-invalidation logic
    /// here. See IcsImportParser for the scope (no RRULE expansion, best-effort TZID). Stays in
    /// the controller rather than moving to SessionService: it touches no persistence at all,
    /// only the uploaded form file and a pure parser.
    /// </summary>
    [HttpPost("import-ics")]
    [RequestSizeLimit(10L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10L * 1024 * 1024)]
    public async Task<ActionResult<IcsImportResultDto>> ImportIcs(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Keine Datei hochgeladen." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8))
            content = await reader.ReadToEndAsync();

        var events = IcsImportParser.Parse(content);
        return Ok(new IcsImportResultDto { Events = events });
    }
}
