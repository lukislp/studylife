using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Metrics API (docs/api/metrics-contract-v1, owner decision: every metric computed in exactly
/// ONE place, served here - the Home Assistant integration reads numbers instead of
/// re-implementing the calculations, see ApiKeyScopes.Ha's Metrics.GetSummary/GetAchievements
/// entries; the client app keeps calling the same StudyLife.Shared functions in-process). Every
/// number here is computed via StudyLife.Shared/CourseCatalog.cs's existing functions - this
/// controller's own job is only: resolve the programme, load its catalog/quotas/settings/
/// sessions/goals/notes/programmes (the same DB reads Index.razor.cs's LoadDataAsync and
/// BackgroundTaskService.Reports already do), and assemble the DTOs. See
/// docs/api/metrics-fixtures.json / MetricsGoldenFixtureTests.cs / MetricsControllerTests.cs for
/// the cross-repo golden-fixture lock with studylife-hacs.
/// </summary>
[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public MetricsController(StudyLifeDb db) => _db = db;

    [HttpGet("summary")]
    public async Task<ActionResult<MetricsSummaryDto>> GetSummary([FromQuery] int? program = null, [FromQuery] DateTime? now = null)
    {
        var asOf = now ?? DateTime.Now;
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync();
        var settings = SettingsController.ToDto(settingsEntity ?? new UserSettingsEntity());

        var resolved = await ResolveProgrammeAsync(program, settingsEntity?.ActiveStudyProgramId);
        if (resolved == null) return NotFound();
        var (programDto, catalog, groupQuotas) = resolved.Value;

        var activeCourseIds = catalog.Select(c => c.Id).ToHashSet();
        var allSessions = await _db.Sessions.AsNoTracking().ToListAsync();
        var scoped = allSessions.Select(SessionsController.ToDto).Where(s => activeCourseIds.Contains(s.CourseId)).ToList();
        var studiedHistory = scoped.Where(s => StudyMetrics.IsStudied(s, asOf)).ToList();

        var goals = await _db.CourseGoals.AsNoTracking()
            .Select(g => CourseGoalsController.ToDto(g)).ToListAsync();
        goals = goals.Where(g => activeCourseIds.Contains(g.CourseId)).ToList();

        var today = asOf.Date;
        var weekStart = StudyMetrics.WeekStartOf(today);
        var weekEnd = weekStart.AddDays(7);
        var weekHours = scoped.Where(s => s.StartTime.Date >= weekStart && s.StartTime.Date < weekEnd)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthHours = scoped.Where(s => s.StartTime.Date >= monthStart)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var totalHours = studiedHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = studiedHistory.Count;
        var streak = StudyMetrics.CalcStreak(studiedHistory.Select(s => s.StartTime), today);
        var longestStreak = StudyMetrics.CalcLongestStreak(studiedHistory.Select(s => s.StartTime));

        var weekQuota = StudyMetrics.CalcQuota(weekHours, settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours);
        var monthQuota = StudyMetrics.CalcQuota(monthHours, settings.MonthlyGoalMinHours, settings.MonthlyGoalMaxHours);

        var ectsTotal = CourseCatalog.CalcTotalEcts(catalog, groupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(catalog, settings.CompletedCourseIds, groupQuotas);

        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(goals
            .Where(g => g.Grade.HasValue)
            .Select(g => new StudyMetrics.GradedCourse(g.Grade!.Value, catalog.FirstOrDefault(c => c.Id == g.CourseId)?.Ects ?? 5)));

        var forecast = StudyMetrics.CalcForecast(
            ectsTotal, ectsEarned, catalog, settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, scoped, asOf);

        var monthComparison = StudyMetrics.CalcMonthComparison(studiedHistory, today);
        var neglected = StudyMetrics.CalcNeglectedCourse(
            catalog, settings.SelectedCourseIds, settings.CompletedCourseIds, studiedHistory, today);
        var weeklyReport = StudyMetrics.CalcLastCompletedWeekReport(studiedHistory, asOf);
        var courseHours = StudyMetrics.CalcCourseHours(
            catalog, settings.SelectedCourseIds, settings.CompletedCourseIds, scoped, asOf);
        var topics = StudyMetrics.CalcTopicsProgress(catalog, settings.SelectedCourseIds, goals);
        var upcomingGoals = StudyMetrics.CalcUpcomingCourseGoals(goals, today);

        return new MetricsSummaryDto
        {
            AsOf = asOf,
            Program = programDto,
            Streak = new MetricsStreakDto { Current = streak, Longest = longestStreak },
            Hours = new MetricsHoursDto { Week = weekHours, Month = monthHours, Total = totalHours, TotalSessions = totalSessions },
            WeekQuota = ToQuotaDto(weekHours, settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, weekQuota),
            MonthQuota = ToQuotaDto(monthHours, settings.MonthlyGoalMinHours, settings.MonthlyGoalMaxHours, monthQuota),
            Ects = new MetricsEctsDto { Earned = ectsEarned, Total = ectsTotal },
            AverageGrade = averageGrade,
            Forecast = new MetricsForecastDto
            {
                Available = forecast.Available,
                AlreadyDone = forecast.AlreadyDone,
                Date = forecast.ForecastDate,
                RecentWeeklyHours = forecast.RecentWeeklyHours,
            },
            MonthComparison = new MetricsMonthComparisonDto
            {
                CurrentMonthHours = monthComparison.CurrentMonthHours,
                PreviousMonthHours = monthComparison.PreviousMonthHours,
                DeltaVsPreviousMonth = monthComparison.DeltaVsPreviousMonth,
                HasYearData = monthComparison.HasYearData,
                SameMonthLastYearHours = monthComparison.SameMonthLastYearHours,
                DeltaVsLastYear = monthComparison.DeltaVsLastYear,
            },
            NeglectedCourse = neglected == null ? null : new MetricsNeglectedCourseDto
            {
                CourseId = neglected.Value.Course.Id,
                CourseName = neglected.Value.Course.Name,
                LastStudied = neglected.Value.LastStudied,
                DaysSince = neglected.Value.LastStudied.HasValue ? (today - neglected.Value.LastStudied.Value.Date).Days : null,
            },
            WeeklyReport = new MetricsWeeklyReportDto
            {
                WeekId = weeklyReport.WeekId,
                Hours = weeklyReport.Hours,
                DeltaVsPreviousWeek = weeklyReport.DeltaVsPreviousWeek,
                TopCourseName = weeklyReport.TopCourseName,
                SessionCount = weeklyReport.SessionCount,
            },
            CourseHours = courseHours
                .OrderByDescending(r => r.Hours)
                .Select(r => new MetricsCourseHoursDto
                {
                    CourseId = r.Course.Id,
                    CourseName = r.Course.Name,
                    CourseColor = r.Course.Color,
                    Hours = r.Hours,
                    SessionCount = r.SessionCount,
                })
                .ToList(),
            Topics = new MetricsTopicsDto { Completed = topics.Completed, Total = topics.Total },
            UpcomingCourseGoals = upcomingGoals
                .Select(g => new MetricsUpcomingGoalDto { CourseId = g.CourseId, CourseName = g.CourseName, TargetDate = g.TargetDate, DaysLeft = g.DaysLeft })
                .ToList(),
        };
    }

    [HttpGet("achievements")]
    public async Task<ActionResult<MetricsAchievementsDto>> GetAchievements([FromQuery] int? program = null)
    {
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync();
        var settings = SettingsController.ToDto(settingsEntity ?? new UserSettingsEntity());

        var resolved = await ResolveProgrammeAsync(program, settingsEntity?.ActiveStudyProgramId);
        if (resolved == null) return NotFound();
        var (_, catalog, groupQuotas) = resolved.Value;

        var activeCourseIds = catalog.Select(c => c.Id).ToHashSet();
        var now = DateTime.Now;
        var studiedHistory = (await _db.Sessions.AsNoTracking().ToListAsync())
            .Select(SessionsController.ToDto)
            .Where(s => activeCourseIds.Contains(s.CourseId) && StudyMetrics.IsStudied(s, now))
            .ToList();

        var notesCount = await _db.Notes.AsNoTracking().CountAsync();
        var programsCompleted = await _db.StudyPrograms.AsNoTracking().CountAsync(p => p.IsCompleted);

        var ectsTotal = CourseCatalog.CalcTotalEcts(catalog, groupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(catalog, settings.CompletedCourseIds, groupQuotas);

        var inputs = AchievementCatalog.BuildInputs(
            studiedHistory, settings.CompletedCourseIds, activeCourseIds,
            settings.WeeklyGoalMinHours, ectsTotal, ectsEarned, notesCount, programsCompleted);

        var tiers = new List<MetricsAchievementTierDto>();
        AddTiers(tiers, AchievementCatalog.HoursKey, AchievementCatalog.HoursTiers, inputs.TotalHours);
        AddTiers(tiers, AchievementCatalog.StreakKey, AchievementCatalog.StreakTiers, inputs.LongestStreak);
        AddTiers(tiers, AchievementCatalog.SessionsKey, AchievementCatalog.SessionsTiers, inputs.TotalSessions);
        AddTiers(tiers, AchievementCatalog.CoursesKey, AchievementCatalog.CoursesTiers, inputs.CoursesCompleted);
        tiers.Add(new MetricsAchievementTierDto { Category = AchievementCatalog.AllCoursesKey, Threshold = 1, Unlocked = inputs.AllCoursesDone, Current = inputs.AllCoursesDone ? 1 : 0 });
        AddTiers(tiers, AchievementCatalog.EarlyBirdKey, AchievementCatalog.EarlyBirdTiers, inputs.EarlyBirdCount);
        AddTiers(tiers, AchievementCatalog.NightOwlKey, AchievementCatalog.NightOwlTiers, inputs.NightOwlCount);
        AddTiers(tiers, AchievementCatalog.WeekendKey, AchievementCatalog.WeekendTiers, inputs.WeekendCount);
        AddTiers(tiers, AchievementCatalog.MarathonKey, AchievementCatalog.MarathonTiers, inputs.LongestSessionHours);
        AddTiers(tiers, AchievementCatalog.PerfectWeekKey, AchievementCatalog.PerfectWeekTiers, inputs.PerfectWeeks);
        AddTiers(tiers, AchievementCatalog.NotesKey, AchievementCatalog.NotesTiers, inputs.NotesCount);
        AddTiers(tiers, AchievementCatalog.CourseDiversityKey, AchievementCatalog.CourseDiversityTiers, inputs.MaxCourseDiversity);
        AddTiers(tiers, AchievementCatalog.ProgramsKey, AchievementCatalog.ProgramsTiers, inputs.ProgramsCompleted);

        return new MetricsAchievementsDto
        {
            Unlocked = tiers.Count(t => t.Unlocked),
            Total = tiers.Count,
            Tiers = tiers,
        };
    }

    private static void AddTiers(List<MetricsAchievementTierDto> tiers, string category, int[] thresholds, double current)
    {
        foreach (var tier in AchievementCatalog.BuildTiers(thresholds, current))
            tiers.Add(new MetricsAchievementTierDto { Category = category, Threshold = tier.Threshold, Unlocked = tier.Unlocked, Current = tier.Current });
    }

    private static MetricsQuotaDto ToQuotaDto(double hours, int targetMin, int targetMax, StudyMetrics.QuotaResult quota) => new()
    {
        Hours = hours,
        TargetMin = targetMin,
        TargetMax = targetMax,
        Percent = quota.Percent,
        MinPercent = quota.MinPercent,
        Warning = quota.Warning,
        MissingHours = quota.MissingHours,
    };

    /// <summary>
    /// Programme resolution: same 0/absent convention as GET /api/courses (CoursesController.GetAll) -
    /// 0 = built-in catalog explicitly, absent = the caller's active programme resolved from
    /// settings, a positive id = that specific programme. UNLIKE GetAll, a non-existent positive
    /// id is NOT silently defaulted to the built-in catalog here (contract: "a non-existent id →
    /// 404") - metrics for a programme that doesn't exist would be actively misleading rather
    /// than a harmless cache-buster fallback. Catalog + quota loading reuses
    /// StudyProgramCatalog.LoadCoursesAsync/LoadGroupQuotasAsync, the same helpers
    /// BackgroundTaskService.Reports and StudyProgramsController already use.
    /// </summary>
    private async Task<(MetricsProgramDto Program, List<CourseDto> Catalog, IReadOnlyDictionary<string, int> GroupQuotas)?> ResolveProgrammeAsync(
        int? programParam, int? activeStudyProgramId)
    {
        int? programId;
        if (programParam.HasValue)
            programId = programParam.Value == 0 ? null : programParam.Value;
        else
            programId = activeStudyProgramId;

        if (programId == null)
        {
            return (
                new MetricsProgramDto { Id = null, Name = CourseCatalog.BuiltInProgramName, IsBuiltIn = true },
                CourseCatalog.AppliedAICourses,
                CourseCatalog.GroupEctsQuotas);
        }

        var program = await _db.StudyPrograms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == programId.Value);
        if (program == null) return null;

        var catalog = await StudyProgramCatalog.LoadCoursesAsync(_db, programId.Value);
        var groupQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(_db, programId.Value);
        return (new MetricsProgramDto { Id = program.Id, Name = program.Name, IsBuiltIn = false }, catalog, groupQuotas);
    }
}
