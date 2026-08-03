using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Loads custom study programs (StudyProgramEntity + CourseGroupEntity +
/// CustomCourseEntity) and maps them to the same CourseDto/quota shapes that the
/// built-in CourseCatalog provides - shared between CoursesController (GET /api/courses),
/// StudyProgramsController, and BackgroundTaskService.Reports (achievement check).
/// </summary>
public static class StudyProgramCatalog
{
    /// <summary>
    /// Offset for the externally visible course ids of custom courses
    /// (CourseDto.Id = Offset + CustomCourseEntity.Id). Prevents collisions with the
    /// hardcoded ids of the built-in catalog (1-62) in Selected-/CompletedCourseIds,
    /// Sessions, and CourseGoals - this keeps ECTS progress cleanly separated per
    /// study program (CalcEctsEarned only counts ids that appear in the
    /// passed-in course catalog).
    /// </summary>
    public const int CustomCourseIdOffset = 100000;

    /// <summary>Courses of a custom study program as a CourseDto list (group name resolved, ids shifted).</summary>
    public static async Task<List<CourseDto>> LoadCoursesAsync(StudyLifeDb db, int programId)
    {
        var groupNames = await db.CourseGroups.AsNoTracking()
            .Where(g => g.StudyProgramId == programId)
            .ToDictionaryAsync(g => g.Id, g => g.Name);

        var courses = await db.CustomCourses.AsNoTracking()
            .Where(c => c.StudyProgramId == programId)
            .OrderBy(c => c.Semester).ThenBy(c => c.Id)
            .ToListAsync();

        return courses.Select(c => new CourseDto
        {
            Id = CustomCourseIdOffset + c.Id,
            Semester = c.Semester,
            Name = c.Name,
            Code = c.Code,
            Color = c.Color,
            Icon = c.Icon,
            Ects = c.Ects,
            Group = c.CourseGroupId.HasValue && groupNames.TryGetValue(c.CourseGroupId.Value, out var name) ? name : null,
            Topics = c.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        }).ToList();
    }

    /// <summary>Group name -> ECTS quota of the study program (semantics like CourseCatalog.GroupEctsQuotas).</summary>
    public static async Task<Dictionary<string, int>> LoadGroupQuotasAsync(StudyLifeDb db, int programId)
    {
        var groups = await db.CourseGroups.AsNoTracking()
            .Where(g => g.StudyProgramId == programId)
            .ToListAsync();
        // ToDictionary instead of ToDictionaryAsync after materialization: group names are
        // unique per POST validation, but defensively the first entry wins.
        var result = new Dictionary<string, int>();
        foreach (var g in groups)
            result.TryAdd(g.Name, g.EctsQuota);
        return result;
    }
}
