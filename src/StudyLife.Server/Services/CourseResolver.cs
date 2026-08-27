using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Central CourseId validation (audit finding M2): resolves a caller-supplied CourseId
/// against the calling user's FULL course universe - the built-in catalog
/// (<see cref="CourseCatalog.AppliedAICourses"/>) plus ALL of that user's custom courses,
/// across EVERY study program they've ever created, not just the currently active one. A
/// session/goal/template can legitimately reference a course of a non-active program (the
/// id was valid when it was fetched via GET /api/courses while that program was active, or
/// Home Assistant's cached course list simply predates a later program switch) - narrowing
/// the lookup to only the active program would incorrectly reject those.
///
/// Before this, every write path accepted any CourseId > 0 with client-supplied
/// CourseName/CourseColor, so a rename in the catalog silently diverged from what was
/// already stamped on old rows, and a bare typo/garbage id was stored without complaint (see
/// docs/ARCHITECTURE.md "Course id validation"). Used by every controller that creates a NEW
/// binding between a row and a course (SessionsController.Create and .Update when the
/// CourseId actually changes, CourseGoalsController.Save on first creation,
/// SessionTemplatesController.Create, CourseResourcesController.Create, NotesController.Create
/// and .Update when a non-null CourseId actually changes) - deliberately NOT used by
/// BackupController's raw restore or JSON import (those intentionally carry historical ids and
/// bypass validation by design, see BackupController's own doc comments) and NOT for a PUT
/// that keeps a row's CourseId unchanged (frozen-at-creation semantics - editing/completing a
/// session, goal, or note of a since-deleted custom course must keep working). Also used, in a
/// deliberately SOFTER form, by BackgroundTaskService.CaptureEnrichment: a course id supplied by
/// studylife-ai that fails to resolve is stored as null with a logged warning instead of
/// rejecting the whole enrichment - a background job degrading gracefully rather than throwing.
/// </summary>
public interface ICourseResolver
{
    /// <summary>
    /// Resolves a CourseId to its current catalog entry, or null if it doesn't exist
    /// anywhere in the calling user's course universe. The custom-course lookup is
    /// automatically scoped to the current user via StudyLifeDb's global query filter on
    /// <see cref="CustomCourseEntity.AuthUserId"/> - no explicit user check needed here.
    /// </summary>
    Task<CourseDto?> ResolveAsync(int courseId);

    /// <summary>
    /// Deliberate contrast with <see cref="ResolveAsync"/>: scoped to only the caller's currently
    /// ACTIVE study program (falling back to the built-in catalog if none is active), and with
    /// Topics/Group populated - narrower scope, wider payload, both on purpose. Used by the
    /// Planner (PlannerController.GenerateExamPlan), which only ever creates NEW work (sessions)
    /// for the program the user is actively studying right now; unlike ResolveAsync's write paths
    /// (session/goal/note edits, etc.) it never needs to keep referencing a course from a program
    /// the caller has since switched away from, and it needs Topics to pick which ones are still
    /// open (ResolveAsync omits Topics/Group - its callers only ever stamp Name/Color onto a row,
    /// so the extra lookup would be pure overhead there).
    /// </summary>
    Task<CourseDto?> ResolveInActiveProgramAsync(int courseId, UserSettingsEntity settings);
}

public class CourseResolver : ICourseResolver
{
    private readonly StudyLifeDb _db;

    public CourseResolver(StudyLifeDb db) => _db = db;

    public async Task<CourseDto?> ResolveAsync(int courseId)
    {
        if (courseId < StudyProgramCatalog.CustomCourseIdOffset)
            return CourseCatalog.AppliedAICourses.FirstOrDefault(c => c.Id == courseId);

        // Custom course: deliberately not scoped to any single StudyProgramId - see the class
        // doc comment for why the lookup must span every program the user owns.
        var rawId = courseId - StudyProgramCatalog.CustomCourseIdOffset;
        var entity = await _db.CustomCourses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == rawId);
        if (entity == null) return null;

        // Group name intentionally omitted (unlike StudyProgramCatalog.LoadCoursesAsync):
        // callers here only ever stamp Name/Color onto a row, so the extra CourseGroups
        // lookup needed to resolve the group's display name would be pure overhead.
        return new CourseDto
        {
            Id = courseId,
            Semester = entity.Semester,
            Name = entity.Name,
            Code = entity.Code,
            Color = entity.Color,
            Icon = entity.Icon,
            Ects = entity.Ects,
        };
    }

    public async Task<CourseDto?> ResolveInActiveProgramAsync(int courseId, UserSettingsEntity settings)
    {
        // Course resolution is program-aware like ProgressController/CoursesController: an
        // active custom study program → its courses (tenant-separated), otherwise the built-in
        // catalog. Previously this endpoint (Home Assistant service "generate_exam_plan") only
        // knew CourseCatalog.AppliedAICourses.
        List<CourseDto> catalog;
        if (settings.ActiveStudyProgramId is int programId
            && await _db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == programId))
        {
            catalog = await StudyProgramCatalog.LoadCoursesAsync(_db, programId);
        }
        else
        {
            catalog = CourseCatalog.AppliedAICourses;
        }
        return catalog.FirstOrDefault(c => c.Id == courseId);
    }
}

/// <summary>Stable, user-facing error text for a CourseId that failed <see cref="ICourseResolver.ResolveAsync"/> -
/// factored out so every write path reports an unknown CourseId identically.</summary>
public static class CourseValidationMessages
{
    public static string UnknownCourseId(int courseId) => $"CourseId {courseId} does not exist.";
}

/// <summary>Stable, user-facing error text for a SessionId that doesn't exist (or doesn't belong
/// to the calling user, indistinguishable via the global query filter - see StudyLifeDb) - the
/// SessionId analogue of <see cref="CourseValidationMessages"/>, used by write paths that reject
/// an unresolvable SessionId (NotesController) rather than silently dropping it (TimerStateController,
/// see its own Save() comment for why that path degrades instead of rejecting).</summary>
public static class SessionValidationMessages
{
    public static string UnknownSessionId(int sessionId) => $"SessionId {sessionId} does not exist.";
}
