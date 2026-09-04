using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The programme a request is scoped to: its course catalog and ECTS group quotas. Every
/// server-side aggregate (MetricsController for Home Assistant/MCP, the dashboard summary for
/// the web client) must filter sessions and goals through exactly this course-id set, the same
/// way the client pages do with AppStateService.GetCoursesAsync - one resolver instead of a copy
/// per controller keeps those sets from drifting apart.
/// </summary>
public sealed record ProgrammeScope(
    int? ProgramId,
    string Name,
    bool IsBuiltIn,
    List<CourseDto> Catalog,
    IReadOnlyDictionary<string, int> GroupQuotas)
{
    /// <summary>Course ids of this programme - the filter every scoped query applies.</summary>
    public HashSet<int> CourseIds => Catalog.Select(c => c.Id).ToHashSet();
}

public interface IProgrammeScopeResolver
{
    /// <summary>
    /// Resolves the programme to scope to. <paramref name="programParam"/> is the optional
    /// request override (0 = the built-in programme, null = "use the active one"); when it is
    /// absent the user's <paramref name="activeStudyProgramId"/> decides. Returns null when the
    /// requested custom programme does not exist (the caller answers 404).
    /// </summary>
    Task<ProgrammeScope?> ResolveAsync(int? programParam, int? activeStudyProgramId);
}

public sealed class ProgrammeScopeResolver : IProgrammeScopeResolver
{
    private readonly StudyLifeDb _db;

    public ProgrammeScopeResolver(StudyLifeDb db) => _db = db;

    public async Task<ProgrammeScope?> ResolveAsync(int? programParam, int? activeStudyProgramId)
    {
        int? programId;
        if (programParam.HasValue)
            programId = programParam.Value == 0 ? null : programParam.Value;
        else
            programId = activeStudyProgramId;

        if (programId == null)
        {
            return new ProgrammeScope(null, CourseCatalog.BuiltInProgramName, true,
                CourseCatalog.AppliedAICourses, CourseCatalog.GroupEctsQuotas);
        }

        var program = await _db.StudyPrograms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == programId.Value);
        if (program == null) return null;
        var catalog = await StudyProgramCatalog.LoadCoursesAsync(_db, programId.Value);
        var groupQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(_db, programId.Value);
        return new ProgrammeScope(program.Id, program.Name, false, catalog, groupQuotas);
    }
}
