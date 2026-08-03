using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// CRUD for session templates (feature "session templates for quickly creating recurring
/// sessions"). Deliberately no PUT/update for the MVP - delete + recreate is enough, see
/// the task description.
/// </summary>
[ApiController]
[Route("api/sessiontemplates")]
public class SessionTemplatesController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public SessionTemplatesController(StudyLifeDb db) => _db = db;

    [HttpGet]
    public async Task<IEnumerable<SessionTemplateDto>> GetAll() =>
        await _db.SessionTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => ToDto(t)).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<SessionTemplateDto>> Create(SessionTemplateDto dto)
    {
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        var entity = new SessionTemplateEntity
        {
            Name = dto.Name.Trim(),
            CourseId = dto.CourseId,
            CourseName = dto.CourseName,
            CourseColor = dto.CourseColor,
            DurationMinutes = dto.DurationMinutes,
            Topic = dto.Topic,
            DefaultWeekday = dto.DefaultWeekday,
            DefaultStartTime = dto.DefaultStartTime,
            CreatedAt = DateTime.UtcNow,
        };
        _db.SessionTemplates.Add(entity);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.SessionTemplates.FindAsync(id);
        if (entity == null) return NotFound();
        _db.SessionTemplates.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Validate(SessionTemplateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "Name must not be empty.";
        if (dto.CourseId <= 0) return "CourseId must be greater than 0.";
        if (string.IsNullOrWhiteSpace(dto.CourseName)) return "CourseName must not be empty.";
        if (dto.DurationMinutes <= 0) return "DurationMinutes must be greater than 0.";
        if (dto.DefaultWeekday is < 0 or > 6) return "DefaultWeekday must be between 0 and 6.";
        return null;
    }

    private static SessionTemplateDto ToDto(SessionTemplateEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        CourseId = e.CourseId,
        CourseName = e.CourseName,
        CourseColor = e.CourseColor,
        DurationMinutes = e.DurationMinutes,
        Topic = e.Topic,
        DefaultWeekday = e.DefaultWeekday,
        DefaultStartTime = e.DefaultStartTime,
        CreatedAt = e.CreatedAt,
    };
}
