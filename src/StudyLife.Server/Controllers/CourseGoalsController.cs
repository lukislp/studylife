using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/coursegoals")]
public class CourseGoalsController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public CourseGoalsController(StudyLifeDb db) => _db = db;

    [HttpGet]
    public async Task<IEnumerable<CourseGoalDto>> GetAll() =>
        await _db.CourseGoals.AsNoTracking().Select(g => ToDto(g)).ToListAsync();

    [HttpPut("{courseId}")]
    public async Task<ActionResult<CourseGoalDto>> Save(int courseId, CourseGoalDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CourseName)) return BadRequest("CourseName must not be empty.");
        if (dto.Grade is < 1.0m or > 5.0m) return BadRequest("Grade must be between 1.0 and 5.0.");

        var entity = await _db.CourseGoals.FirstOrDefaultAsync(g => g.CourseId == courseId);
        if (entity == null)
        {
            entity = new CourseGoalEntity { CourseId = courseId };
            _db.CourseGoals.Add(entity);
        }
        entity.CourseName = dto.CourseName;
        entity.TargetDate = dto.TargetDate;
        entity.CompletionNote = dto.CompletionNote;
        entity.CompletedAt = dto.CompletedAt;
        entity.Grade = dto.Grade;
        entity.CompletedTopics = dto.CompletedTopics;
        entity.Tag = dto.Tag;
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    [HttpDelete("{courseId}")]
    public async Task<IActionResult> Delete(int courseId)
    {
        var entity = await _db.CourseGoals.FirstOrDefaultAsync(g => g.CourseId == courseId);
        if (entity == null) return NotFound();
        _db.CourseGoals.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // internal instead of private: reused by BackupController (JSON export) so the export
    // projection doesn't have to duplicate the same mapping a second time.
    internal static CourseGoalDto ToDto(CourseGoalEntity e) => new()
    {
        CourseId = e.CourseId,
        CourseName = e.CourseName,
        TargetDate = e.TargetDate,
        CompletionNote = e.CompletionNote,
        CompletedAt = e.CompletedAt,
        Grade = e.Grade,
        CompletedTopics = e.CompletedTopics,
        Tag = e.Tag,
    };
}
