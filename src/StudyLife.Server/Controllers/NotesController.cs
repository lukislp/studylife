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

    public NotesController(StudyLifeDb db, INoteSearchStrategy searchStrategy)
    {
        _db = db;
        _searchStrategy = searchStrategy;
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
    public async Task<NoteDto> Create(NoteDto dto)
    {
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
