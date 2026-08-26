using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/coursegoals")]
public class CourseGoalsController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly ICourseResolver _courseResolver;

    public CourseGoalsController(StudyLifeDb db, ICourseResolver courseResolver)
    {
        _db = db;
        _courseResolver = courseResolver;
    }

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
            // Audit finding M2: a NEW goal binds a fresh CourseId, so it must resolve against
            // the user's full course universe (see CourseResolver) - CourseName is then derived
            // from the resolved course, not taken from the client. An UPDATE of an EXISTING
            // goal, below, never re-validates or re-derives: the route parameter IS the goal's
            // CourseId (there is no way to change it via this endpoint), so frozen-at-creation
            // semantics apply automatically - editing a goal of a since-deleted custom course
            // must keep working, and a later catalog rename must not rewrite it.
            var course = await _courseResolver.ResolveAsync(courseId);
            if (course == null) return BadRequest(CourseValidationMessages.UnknownCourseId(courseId));

            entity = new CourseGoalEntity { CourseId = courseId, CourseName = course.Name };
            _db.CourseGoals.Add(entity);
        }
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
