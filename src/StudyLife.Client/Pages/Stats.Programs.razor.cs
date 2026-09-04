using System.Net.Http.Json;
using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsProgramComparisonCard.ProgramRow> _programComparisonRows = new();

    /// <summary>
    /// Loads what the cross-programme comparison needs: the programme list plus, per programme,
    /// its course catalog and its ECTS group quotas. The comparison itself (hours, ECTS status,
    /// average grade) is computed by StatsSummaryBuilder from these - this method only fetches,
    /// so a server-side twin can source the exact same input from the database in one go instead
    /// of this N+1 fan-out. With only the built-in programme, the per-programme fetches are
    /// skipped entirely (the card then stays hidden anyway).
    ///
    /// Every programme's fetch pair is independent of every other's, so they all run concurrently
    /// (Task.WhenAll preserves the input order in its result array) instead of one programme, and
    /// one fetch within it, after another. With N programmes this turns up to 2N sequential round
    /// trips into one.
    /// </summary>
    private async Task<(List<StudyProgramSummaryDto> Programs, List<StatsProgramCatalogDto> Catalogs)> LoadProgramCatalogsAsync()
    {
        var programs = await State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms") ?? new();
        if (programs.Count < 2) return (programs, new List<StatsProgramCatalogDto>());

        var catalogs = await Task.WhenAll(programs.Select(async program =>
        {
            // Same URL convention as AppStateService.GetCoursesAsync (?program= as
            // cache buster, 0 = built-in catalog).
            var coursesTask = State.GetJsonCachedAsync<List<CourseDto>>($"api/courses?program={program.Id ?? 0}");
            // The built-in programme has no DB row, so it has no detail endpoint either - the
            // builder uses the static CourseCatalog.GroupEctsQuotas for it.
            var detailTask = program.Id is int programId
                ? Http.GetFromJsonAsync<StudyProgramDetailDto>($"api/studyprograms/{programId}")
                : Task.FromResult<StudyProgramDetailDto?>(null);

            var courses = await coursesTask ?? new();
            StudyProgramDetailDto? detail = null;
            // Fetch error: groups count as defensively full, like AppStateService.GetActiveGroupQuotasAsync.
            try { detail = await detailTask; }
            catch { /* ignore */ }

            return new StatsProgramCatalogDto
            {
                ProgramId = program.Id,
                Courses = courses,
                GroupQuotas = detail?.GroupEctsQuotas ?? new Dictionary<string, int>(),
            };
        }));

        return (programs, catalogs.ToList());
    }

    /// <summary>Programme rows -> the card's shape. Nothing here is localized: the names come
    /// from the programme rows and the grade label from StudyMetrics.FormatGrade.</summary>
    private void ApplyProgramComparison(List<StatsProgramRowDto> rows) =>
        _programComparisonRows = rows
            .Select(r => new StatsProgramComparisonCard.ProgramRow(
                r.Name, r.IsActive, r.IsCompleted, r.Hours, r.SessionCount,
                r.EctsEarned, r.EctsTotal, r.GradeLabel, r.BarPercent))
            .ToList();
}
