using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/studyprograms domain operations: the switcher list (incl. the synthetic built-in
/// entry), the group-quota detail, the manual completion flag, creating a whole program in one
/// transaction, and deleting one with its groups and courses.
/// <see cref="StudyProgramsController"/> keeps only route binding and status-code mapping.
/// </summary>
public interface IStudyProgramService
{
    Task<List<StudyProgramSummaryDto>> GetSummariesAsync();
    Task<ServiceResult<StudyProgramDetailDto>> GetAsync(int id);
    Task<ServiceResult<StudyProgramSummaryDto>> SetCompletedAsync(int id, SetStudyProgramCompletedDto request);
    Task<ServiceResult<StudyProgramSummaryDto>> CreateAsync(CreateStudyProgramRequestDto request);
    Task<ServiceResult> DeleteAsync(int id);
}

public class StudyProgramService(
    StudyLifeDb db,
    SettingsCacheVersion settingsCacheVersion,
    WebhooksProxyClient webhooks,
    ICurrentUserAccessor currentUser) : IStudyProgramService
{
    public Task<List<StudyProgramSummaryDto>> GetSummariesAsync() => LoadSummariesAsync(db);

    /// <summary>
    /// Core of <see cref="GetSummariesAsync"/> - also reused by the summary endpoints
    /// (Dashboard/Report/Setup/Stats/Wrapped, all of which serve the same "GET /api/studyprograms"
    /// data the client's LoadDataAsync fetches), so the synthetic built-in entry stays defined in
    /// exactly one place instead of being copy-pasted at every call site. Static and taking the
    /// context explicitly because those call sites already hold their own <see cref="StudyLifeDb"/>
    /// and need nothing else this service injects.
    /// </summary>
    internal static async Task<List<StudyProgramSummaryDto>> LoadSummariesAsync(StudyLifeDb db)
    {
        var dismissed = await db.Settings.AsNoTracking()
            .Select(s => s.BuiltInProgramDismissed).FirstOrDefaultAsync();
        var result = new List<StudyProgramSummaryDto>();
        if (!dismissed)
            result.Add(new() { Id = null, Name = CourseCatalog.BuiltInProgramName, IsBuiltIn = true });
        var custom = await db.StudyPrograms.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new StudyProgramSummaryDto { Id = p.Id, Name = p.Name, IsBuiltIn = false, IsCompleted = p.IsCompleted })
            .ToListAsync();
        result.AddRange(custom);
        return result;
    }

    public async Task<ServiceResult<StudyProgramDetailDto>> GetAsync(int id)
    {
        var program = await db.StudyPrograms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return ServiceResult<StudyProgramDetailDto>.NotFound();
        return ServiceResult<StudyProgramDetailDto>.Success(new StudyProgramDetailDto
        {
            Id = program.Id,
            Name = program.Name,
            GroupEctsQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(db, id),
        });
    }

    /// <summary>
    /// Sets/removes the purely MANUAL completion flag of a custom study program. No
    /// automation: the flag is only ever changed through here, never, e.g., at 100% ECTS.
    /// The built-in study program has no DB row and therefore cannot be marked (404).
    /// </summary>
    public async Task<ServiceResult<StudyProgramSummaryDto>> SetCompletedAsync(int id, SetStudyProgramCompletedDto request)
    {
        var program = await db.StudyPrograms.FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return ServiceResult<StudyProgramSummaryDto>.NotFound();
        var wasCompleted = program.IsCompleted;
        program.IsCompleted = request.IsCompleted;
        await db.SaveChangesAsync();
        if (program.IsCompleted && !wasCompleted)
        {
            _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.StudyProgramCompleted,
                new { id = program.Id, name = program.Name }, CancellationToken.None);
        }
        return ServiceResult<StudyProgramSummaryDto>.Success(
            new StudyProgramSummaryDto { Id = program.Id, Name = program.Name, IsBuiltIn = false, IsCompleted = program.IsCompleted });
    }

    /// <summary>
    /// Deletes a custom study program along with its elective groups and courses. The
    /// built-in study program has no DB row and therefore cannot be deleted (the route only
    /// matches int ids anyway, "no program" is never reached through here). Refuses (400) to
    /// delete a user's LAST remaining custom program once they've dismissed the built-in one
    /// (SettingsController.DismissBuiltInProgram) - with no built-in fallback left either,
    /// that would leave ActiveStudyProgramId pointing at nothing.
    ///
    /// Deliberately NOT deleted along with it: CourseGoalEntity (grades/deadlines) and
    /// StudySessionEntity (study sessions) - both reference courses only via a bare int
    /// CourseId without an FK (see StudyLifeDb.cs), and the value the client sends for that
    /// is the externally shifted CourseDto.Id (StudyProgramCatalog.CustomCourseIdOffset +
    /// CustomCourseEntity.Id), not the raw CustomCourseEntity.Id. There is no place anywhere
    /// in the codebase yet that deletes a single course while goals/sessions for it remain -
    /// deleting a program is the first such case. All existing consumers of these ids
    /// (CourseCatalog.CalcEctsEarned &amp; co.) iterate over the current course catalog and simply
    /// don't look up referenced but no-longer-existing courses (HashSet.Contains pattern) -
    /// orphaned goals/sessions of a deleted program are thereby quietly ignored everywhere
    /// missing courses are already tolerated today, instead of throwing an error. Explicitly
    /// deleting them as well would also silently destroy notes/history that the user doesn't
    /// expect to lose separately from deleting the program - when in doubt, data deletion
    /// stays minimally invasive.
    /// </summary>
    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var program = await db.StudyPrograms.FirstOrDefaultAsync(p => p.Id == id);
        if (program == null) return ServiceResult.NotFound();
        var programId = program.Id;
        var programName = program.Name;

        // With the built-in program dismissed (SettingsController.DismissBuiltInProgram), this
        // user has no other fallback - refuse to delete their last remaining program so
        // ActiveStudyProgramId is never left pointing at nothing.
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings?.BuiltInProgramDismissed == true
            && !await db.StudyPrograms.AnyAsync(p => p.Id != id))
            return ServiceResult.Invalid("Cannot delete your only study program while the default template is hidden.");

        await using var transaction = await db.Database.BeginTransactionAsync();

        // Courses first, then groups, then the program itself - no navigation properties/
        // cascade-delete configuration in this codebase's style (see CreateAsync below), so
        // manually in the correct order of referential dependency.
        var courses = await db.CustomCourses.Where(c => c.StudyProgramId == id).ToListAsync();
        db.CustomCourses.RemoveRange(courses);
        await db.SaveChangesAsync();

        var groups = await db.CourseGroups.Where(g => g.StudyProgramId == id).ToListAsync();
        db.CourseGroups.RemoveRange(groups);
        await db.SaveChangesAsync();

        db.StudyPrograms.Remove(program);
        await db.SaveChangesAsync();

        // If the deleted program was active, the selection falls back to the built-in study
        // program (ActiveStudyProgramId == null) - otherwise the client would point to a
        // program that no longer exists.
        if (settings != null && settings.ActiveStudyProgramId == id)
        {
            settings.ActiveStudyProgramId = null;
            await db.SaveChangesAsync();
            // SettingsController.Get() caches for 15s via SettingsCacheVersion - without this
            // bump, a client polling shortly after the deletion would still see the old (now
            // invalid) ActiveStudyProgramId.
            await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        }

        await transaction.CommitAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.StudyProgramDeleted,
            new { id = programId, name = programName }, CancellationToken.None);
        return ServiceResult.Success();
    }

    public async Task<ServiceResult<StudyProgramSummaryDto>> CreateAsync(CreateStudyProgramRequestDto request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return ServiceResult<StudyProgramSummaryDto>.Invalid("Name must not be empty.");
        if (name.Length > 100)
            return ServiceResult<StudyProgramSummaryDto>.Invalid("Name must be at most 100 characters long.");
        if (request.Courses == null || request.Courses.Count == 0)
            return ServiceResult<StudyProgramSummaryDto>.Invalid("At least one course is required.");
        if (request.Courses.Count > 300)
            return ServiceResult<StudyProgramSummaryDto>.Invalid("At most 300 courses per study program.");
        var groups = request.Groups ?? new List<CreateStudyProgramGroupDto>();
        if (groups.Count > 50)
            return ServiceResult<StudyProgramSummaryDto>.Invalid("At most 50 elective groups per study program.");

        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var groupName = group.Name?.Trim() ?? "";
            if (groupName.Length is 0 or > 100)
                return ServiceResult<StudyProgramSummaryDto>.Invalid("Every elective group needs a name (max. 100 characters).");
            if (group.EctsQuota < 1)
                return ServiceResult<StudyProgramSummaryDto>.Invalid("Every elective group's ECTS quota must be greater than 0.");
            if (!groupNames.Add(groupName))
                return ServiceResult<StudyProgramSummaryDto>.Invalid($"Elective group '{groupName}' is duplicated.");
        }

        foreach (var course in request.Courses)
        {
            if (string.IsNullOrWhiteSpace(course.Name))
                return ServiceResult<StudyProgramSummaryDto>.Invalid("Every course needs a name.");
            if (course.Ects < 1)
                return ServiceResult<StudyProgramSummaryDto>.Invalid($"Course '{course.Name.Trim()}': ECTS must be greater than 0.");
            if (course.Semester is < 1 or > 20)
                return ServiceResult<StudyProgramSummaryDto>.Invalid($"Course '{course.Name.Trim()}': semester must be between 1 and 20.");
            if (!string.IsNullOrWhiteSpace(course.Group) && !groupNames.Contains(course.Group.Trim()))
                return ServiceResult<StudyProgramSummaryDto>.Invalid($"Course '{course.Name.Trim()}': elective group '{course.Group.Trim()}' is not defined.");
        }

        // Program → groups → courses in one transaction, because the children's FK ids
        // are only known after the respective SaveChanges (codebase style: no navigation
        // properties, only bare int FKs).
        await using var transaction = await db.Database.BeginTransactionAsync();

        var program = new StudyProgramEntity { Name = name, CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        var groupEntities = groups.Select(g => new CourseGroupEntity
        {
            StudyProgramId = program.Id,
            Name = g.Name.Trim(),
            EctsQuota = g.EctsQuota,
        }).ToList();
        db.CourseGroups.AddRange(groupEntities);
        await db.SaveChangesAsync();

        var groupIdsByName = groupEntities.ToDictionary(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        db.CustomCourses.AddRange(request.Courses.Select(c => new CustomCourseEntity
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
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.StudyProgramCreated,
            new { id = program.Id, name = program.Name }, CancellationToken.None);
        return ServiceResult<StudyProgramSummaryDto>.Success(
            new StudyProgramSummaryDto { Id = program.Id, Name = program.Name, IsBuiltIn = false });
    }
}
