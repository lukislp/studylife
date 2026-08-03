using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Custom study programs: list for the switcher in setup, detail (group quotas) for the
/// client's program-aware ECTS calculation, and creating a complete study program in one
/// call. The fixed built-in study program has no DB row and only appears as a synthetic
/// list entry (Id == null). Switching the active study program does NOT go through here,
/// but as a normal settings field (UserSettings.ActiveStudyProgramId, PUT /api/settings) -
/// consistent with all other settings.
/// </summary>
[ApiController]
[Route("api/studyprograms")]
public class StudyProgramsController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly SettingsCacheVersion _settingsCacheVersion;

    public StudyProgramsController(StudyLifeDb db, SettingsCacheVersion settingsCacheVersion)
    {
        _db = db;
        _settingsCacheVersion = settingsCacheVersion;
    }

    [HttpGet]
    public async Task<ActionResult<List<StudyProgramSummaryDto>>> GetAll()
    {
        var result = new List<StudyProgramSummaryDto>
        {
            new() { Id = null, Name = CourseCatalog.BuiltInProgramName, IsBuiltIn = true },
        };
        var custom = await _db.StudyPrograms.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new StudyProgramSummaryDto { Id = p.Id, Name = p.Name, IsBuiltIn = false, IsCompleted = p.IsCompleted })
            .ToListAsync();
        result.AddRange(custom);
        return result;
    }

    /// <summary>
    /// Sets/removes the purely MANUAL completion flag of a custom study program. No
    /// automation: the flag is only ever changed through here, never, e.g., at 100% ECTS.
    /// The built-in study program has no DB row and therefore cannot be marked (404).
    /// </summary>
    [HttpPut("{id:int}/completed")]
    public async Task<ActionResult<StudyProgramSummaryDto>> SetCompleted(int id, SetStudyProgramCompletedDto request)
    {
        var program = await _db.StudyPrograms.FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return NotFound();
        program.IsCompleted = request.IsCompleted;
        await _db.SaveChangesAsync();
        return new StudyProgramSummaryDto { Id = program.Id, Name = program.Name, IsBuiltIn = false, IsCompleted = program.IsCompleted };
    }

    /// <summary>
    /// Deletes a custom study program along with its elective groups and courses. The
    /// built-in study program has no DB row and therefore cannot be deleted (the route only
    /// matches int ids anyway, "no program" is never reached through here).
    ///
    /// Deliberately NOT deleted along with it: CourseGoalEntity (grades/deadlines) and
    /// StudySessionEntity (study sessions) - both reference courses only via a bare int
    /// CourseId without an FK (see StudyLifeDb.cs), and the value the client sends for that
    /// is the externally shifted CourseDto.Id (StudyProgramCatalog.CustomCourseIdOffset +
    /// CustomCourseEntity.Id), not the raw CustomCourseEntity.Id. There is no place anywhere
    /// in the codebase yet that deletes a single course while goals/sessions for it remain -
    /// deleting a program is the first such case. All existing consumers of these ids
    /// (CourseCatalog.CalcEctsEarned & co.) iterate over the current course catalog and simply
    /// don't look up referenced but no-longer-existing courses (HashSet.Contains pattern) -
    /// orphaned goals/sessions of a deleted program are thereby quietly ignored everywhere
    /// missing courses are already tolerated today, instead of throwing an error. Explicitly
    /// deleting them as well would also silently destroy notes/history that the user doesn't
    /// expect to lose separately from deleting the program - when in doubt, data deletion
    /// stays minimally invasive.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var program = await _db.StudyPrograms.FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return NotFound();

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Courses first, then groups, then the program itself - no navigation properties/
        // cascade-delete configuration in this codebase's style (see Create above), so
        // manually in the correct order of referential dependency.
        var courses = await _db.CustomCourses.Where(c => c.StudyProgramId == id).ToListAsync();
        _db.CustomCourses.RemoveRange(courses);
        await _db.SaveChangesAsync();

        var groups = await _db.CourseGroups.Where(g => g.StudyProgramId == id).ToListAsync();
        _db.CourseGroups.RemoveRange(groups);
        await _db.SaveChangesAsync();

        _db.StudyPrograms.Remove(program);
        await _db.SaveChangesAsync();

        // If the deleted program was active, the selection falls back to the built-in study
        // program (ActiveStudyProgramId == null) - otherwise the client would point to a
        // program that no longer exists.
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings != null && settings.ActiveStudyProgramId == id)
        {
            settings.ActiveStudyProgramId = null;
            await _db.SaveChangesAsync();
            // SettingsController.Get() caches for 15s via SettingsCacheVersion - without this
            // bump, a client polling shortly after the deletion would still see the old (now
            // invalid) ActiveStudyProgramId.
            _settingsCacheVersion.Value++;
        }

        await transaction.CommitAsync();
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StudyProgramDetailDto>> Get(int id)
    {
        var program = await _db.StudyPrograms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return NotFound();
        return new StudyProgramDetailDto
        {
            Id = program.Id,
            Name = program.Name,
            GroupEctsQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(_db, id),
        };
    }

    [HttpPost]
    public async Task<ActionResult<StudyProgramSummaryDto>> Create(CreateStudyProgramRequestDto request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest("Name must not be empty.");
        if (name.Length > 100)
            return BadRequest("Name must be at most 100 characters long.");
        if (request.Courses == null || request.Courses.Count == 0)
            return BadRequest("At least one course is required.");
        if (request.Courses.Count > 300)
            return BadRequest("At most 300 courses per study program.");
        var groups = request.Groups ?? new List<CreateStudyProgramGroupDto>();
        if (groups.Count > 50)
            return BadRequest("At most 50 elective groups per study program.");

        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var groupName = group.Name?.Trim() ?? "";
            if (groupName.Length is 0 or > 100)
                return BadRequest("Every elective group needs a name (max. 100 characters).");
            if (group.EctsQuota < 1)
                return BadRequest("Every elective group's ECTS quota must be greater than 0.");
            if (!groupNames.Add(groupName))
                return BadRequest($"Elective group '{groupName}' is duplicated.");
        }

        foreach (var course in request.Courses)
        {
            if (string.IsNullOrWhiteSpace(course.Name))
                return BadRequest("Every course needs a name.");
            if (course.Ects < 1)
                return BadRequest($"Course '{course.Name.Trim()}': ECTS must be greater than 0.");
            if (course.Semester is < 1 or > 20)
                return BadRequest($"Course '{course.Name.Trim()}': semester must be between 1 and 20.");
            if (!string.IsNullOrWhiteSpace(course.Group) && !groupNames.Contains(course.Group.Trim()))
                return BadRequest($"Course '{course.Name.Trim()}': elective group '{course.Group.Trim()}' is not defined.");
        }

        // Program → groups → courses in one transaction, because the children's FK ids
        // are only known after the respective SaveChanges (codebase style: no navigation
        // properties, only bare int FKs).
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var program = new StudyProgramEntity { Name = name, CreatedAt = DateTime.UtcNow };
        _db.StudyPrograms.Add(program);
        await _db.SaveChangesAsync();

        var groupEntities = groups.Select(g => new CourseGroupEntity
        {
            StudyProgramId = program.Id,
            Name = g.Name.Trim(),
            EctsQuota = g.EctsQuota,
        }).ToList();
        _db.CourseGroups.AddRange(groupEntities);
        await _db.SaveChangesAsync();

        var groupIdsByName = groupEntities.ToDictionary(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        _db.CustomCourses.AddRange(request.Courses.Select(c => new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = c.Semester,
            Name = c.Name.Trim(),
            Code = c.Code?.Trim() ?? "",
            Color = string.IsNullOrWhiteSpace(c.Color) ? "#6C5CE7" : c.Color.Trim(),
            Icon = string.IsNullOrWhiteSpace(c.Icon) ? "📚" : c.Icon.Trim(),
            Ects = c.Ects,
            CourseGroupId = string.IsNullOrWhiteSpace(c.Group) ? null : groupIdsByName[c.Group.Trim()],
            // Comma-separated like CourseGoalEntity.CompletedTopics; the format can't carry
            // commas within topic names by design, so filter them out here.
            Topics = string.Join(",", c.Topics?
                .Select(t => t.Trim().Replace(",", ""))
                .Where(t => t.Length > 0) ?? Enumerable.Empty<string>()),
        }));
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new StudyProgramSummaryDto { Id = program.Id, Name = program.Name, IsBuiltIn = false };
    }
}
