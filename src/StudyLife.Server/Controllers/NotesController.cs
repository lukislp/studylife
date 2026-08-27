using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/notes")]
public class NotesController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly INoteSearchStrategy _searchStrategy;
    private readonly ICourseResolver _courseResolver;

    public NotesController(StudyLifeDb db, INoteSearchStrategy searchStrategy, ICourseResolver courseResolver)
    {
        _db = db;
        _searchStrategy = searchStrategy;
        _courseResolver = courseResolver;
    }

    [HttpGet]
    public async Task<IEnumerable<NoteDto>> GetAll() =>
        await _db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).Select(n => ToDto(n)).ToListAsync();

    /// <summary>
    /// Full-text search over title + content, provider-dependent (SQLite FTS5 vs. Postgres
    /// tsvector, see INoteSearchStrategy) - which implementation is active is decided by
    /// Program.cs analogous to the Database:Provider switch.
    /// </summary>
    [HttpGet("search")]
    public async Task<IEnumerable<NoteDto>> Search([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return Enumerable.Empty<NoteDto>();

        var notes = await _searchStrategy.SearchAsync(_db, q);
        return notes.Select(ToDto);
    }

    [HttpPost]
    public async Task<ActionResult<NoteDto>> Create(NoteDto dto)
    {
        // A note without a course is legit (e.g. a fresh capture before the AI enrichment or the
        // user assigns one) - only a NON-NULL CourseId/SessionId is validated, same "set means
        // checked" contract as the CourseId re-validation on Update below.
        if (dto.CourseId is { } courseId)
        {
            var course = await _courseResolver.ResolveAsync(courseId);
            if (course == null) return BadRequest(CourseValidationMessages.UnknownCourseId(courseId));
        }
        if (dto.SessionId is { } sessionId)
        {
            var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId);
            if (!sessionExists) return BadRequest(SessionValidationMessages.UnknownSessionId(sessionId));
        }

        var entity = new NoteEntity
        {
            Title = dto.Title,
            Content = dto.Content,
            CourseId = dto.CourseId,
            SessionId = dto.SessionId,
            IsMarkdown = dto.IsMarkdown,
            SourceUrl = dto.SourceUrl,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _db.Notes.Add(entity);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, NoteDto dto)
    {
        var entity = await _db.Notes.FindAsync(id);
        if (entity == null) return NotFound();

        // Frozen-at-creation exemption, mirroring SessionsController.Update's CourseId handling
        // (audit finding M2 follow-up): only a CourseId/SessionId that actually CHANGED to a new
        // non-null value is re-validated - editing an old note still bound to a since-deleted
        // custom course (or a session that has since been removed) must keep working. Changing
        // TO null (detaching) never needs validation either way.
        if (dto.CourseId != entity.CourseId && dto.CourseId is { } courseId)
        {
            var course = await _courseResolver.ResolveAsync(courseId);
            if (course == null) return BadRequest(CourseValidationMessages.UnknownCourseId(courseId));
        }
        if (dto.SessionId != entity.SessionId && dto.SessionId is { } sessionId)
        {
            var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId);
            if (!sessionExists) return BadRequest(SessionValidationMessages.UnknownSessionId(sessionId));
        }

        entity.Title = dto.Title;
        entity.Content = dto.Content;
        entity.CourseId = dto.CourseId;
        entity.SessionId = dto.SessionId;
        entity.IsMarkdown = dto.IsMarkdown;
        entity.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.Notes.FindAsync(id);
        if (entity == null) return NotFound();
        _db.Notes.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // internal instead of private: reused by BackupController (JSON export), so the export
    // projection doesn't have to duplicate the same mapping a second time.
    internal static NoteDto ToDto(NoteEntity e) => new()
    {
        Id = e.Id,
        Title = e.Title,
        Content = e.Content,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        CourseId = e.CourseId,
        SessionId = e.SessionId,
        IsMarkdown = e.IsMarkdown,
        SourceUrl = e.SourceUrl,
        Tags = e.Tags,
        Summary = e.Summary,
        RelatedNoteIds = CommaSeparatedIds.Parse(e.RelatedNoteIds)
    };
}
