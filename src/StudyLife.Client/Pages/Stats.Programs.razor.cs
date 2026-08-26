using System.Net.Http.Json;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsProgramComparisonCard.ProgramRow> _programComparisonRows = new();

    /// <summary>
    /// Programmes side by side (hours, ECTS status, average grade) - the only card on this
    /// page that looks beyond the active programme, which is why it gets the
    /// UNFILTERED history/goal list from OnInitializedAsync. With only the built-in
    /// programme, the card stays completely hidden (no "comparison" of a single entry).
    /// </summary>
    private async Task BuildProgramComparisonAsync(List<StudySessionDto> historyAll, List<CourseGoalDto> goalsAll, UserSettings settings)
    {
        _programComparisonRows = new();
        var programs = await State.GetJsonCachedAsync<List<StudyProgramSummaryDto>>("api/studyprograms") ?? new();
        if (programs.Count < 2) return;

        // Every programme's row - and, within it, its courses/detail fetch pair - is independent
        // of every other's, so build them all concurrently (Task.WhenAll preserves the input
        // order in its result array) instead of one programme, and one fetch within it, after
        // another. With N programmes this turns up to 2N sequential round trips into one.
        var rows = await Task.WhenAll(programs.Select(async program =>
        {
            // Same URL convention as AppStateService.GetCoursesAsync (?program= as
            // cache buster, 0 = built-in catalog).
            var coursesTask = State.GetJsonCachedAsync<List<CourseDto>>($"api/courses?program={program.Id ?? 0}");
            var detailTask = program.Id is int programId
                ? Http.GetFromJsonAsync<StudyProgramDetailDto>($"api/studyprograms/{programId}")
                : Task.FromResult<StudyProgramDetailDto?>(null);

            var courses = await coursesTask ?? new();
            var courseIds = courses.Select(c => c.Id).ToHashSet();

            var studied = historyAll
                .Where(s => courseIds.Contains(s.CourseId) && StudyMetrics.IsStudied(s, DateTime.Now))
                .ToList();
            var hours = studied.Sum(s => (s.EndTime - s.StartTime).TotalHours);

            // Programme-aware ECTS calculation like in OnInitializedAsync, just per programme
            // instead of only for the active one (built-in: static quotas, otherwise the detail endpoint).
            IReadOnlyDictionary<string, int> quotas;
            if (program.Id is int)
            {
                StudyProgramDetailDto? detail = null;
                // Fetch error: groups count as defensively full, like AppStateService.GetActiveGroupQuotasAsync.
                try { detail = await detailTask; }
                catch { /* ignore */ }
                quotas = detail?.GroupEctsQuotas ?? new Dictionary<string, int>();
            }
            else
            {
                quotas = CourseCatalog.GroupEctsQuotas;
            }
            var ectsTotal = CourseCatalog.CalcTotalEcts(courses, quotas);
            var ectsEarned = CourseCatalog.CalcEctsEarned(courses, settings.CompletedCourseIds, quotas);

            var avgGrade = StudyMetrics.CalcWeightedAverageGrade(goalsAll
                .Where(g => g.Grade.HasValue && courseIds.Contains(g.CourseId))
                .Select(g => (g.Grade!.Value, courses.First(c => c.Id == g.CourseId).Ects)));

            return new StatsProgramComparisonCard.ProgramRow(
                program.Name,
                program.Id == settings.ActiveStudyProgramId,
                program.IsCompleted,
                hours,
                studied.Count,
                ectsEarned,
                ectsTotal,
                avgGrade.HasValue ? StudyMetrics.FormatGrade(avgGrade.Value) : null,
                0);
        }));

        var maxHours = Math.Max(1.0, rows.Max(r => r.Hours));
        _programComparisonRows = rows
            .Select(r => r with { BarPercent = Math.Min(100, r.Hours / maxHours * 100) })
            .ToList();
    }
}
