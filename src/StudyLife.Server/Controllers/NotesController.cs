using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/notes")]
public class NotesController : ControllerBase
{
    private readonly INoteService _notes;

    public NotesController(INoteService notes) => _notes = notes;

    [HttpGet]
    public async Task<IEnumerable<NoteDto>> GetAll() => await _notes.GetAllAsync();

    /// <summary>
    /// Full-text search over title + content, provider-dependent (SQLite FTS5 vs. Postgres
    /// tsvector, see INoteSearchStrategy) - which implementation is active is decided by
    /// Program.cs analogous to the Database:Provider switch.
    /// </summary>
    [HttpGet("search")]
    public async Task<IEnumerable<NoteDto>> Search([FromQuery] string? q) => await _notes.SearchAsync(q);

    [HttpPost]
    public async Task<ActionResult<NoteDto>> Create(NoteDto dto) =>
        (await _notes.CreateAsync(dto)).ToActionResult(this);

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, NoteDto dto) =>
        (await _notes.UpdateAsync(id, dto)).ToOkResult(this);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        (await _notes.DeleteAsync(id)).ToNoContentResult(this);
}
