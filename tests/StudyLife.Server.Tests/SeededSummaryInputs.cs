// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Assembles the dashboard and stats builder inputs for one seeded account from the database the
/// way the summary endpoints do, so tests of the sample dataset (demo and sample accounts) check
/// the numbers the pages would show.
/// </summary>
internal static class SeededSummaryInputs
{
    public sealed record Inputs(DashboardSummaryInput Dashboard, StatsSummaryInput Stats);

    public static async Task<Inputs> LoadAsync(StudyLifeDb db, int userId, DateTime now)
    {
        var settingsEntity = await db.Settings.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.AuthUserId == userId);
        var settings = SettingsService.ToDto(settingsEntity);
        var sessions = (await db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.AuthUserId == userId).ToListAsync())
            .Select(SessionService.ToDto).ToList();
        var goals = (await db.CourseGoals.IgnoreQueryFilters().AsNoTracking().Where(g => g.AuthUserId == userId).ToListAsync())
            .Select(CourseGoalService.ToDto).ToList();
        var notes = (await db.Notes.IgnoreQueryFilters().AsNoTracking().Where(n => n.AuthUserId == userId).ToListAsync())
            .Select(NoteService.ToDto).ToList();

        List<CourseDto> courses;
        IReadOnlyDictionary<string, int> quotas;
        if (settingsEntity.ActiveStudyProgramId is null)
        {
            courses = CourseCatalog.AppliedAICourses.ToList();
            quotas = CourseCatalog.GroupEctsQuotas;
        }
        else
        {
            courses = (await db.CustomCourses.IgnoreQueryFilters().AsNoTracking()
                    .Where(c => c.AuthUserId == userId && c.StudyProgramId == settingsEntity.ActiveStudyProgramId)
                    .ToListAsync())
                .Select(c => new CourseDto
                {
                    Id = StudyProgramCatalog.CustomCourseIdOffset + c.Id,
                    Semester = c.Semester,
                    Name = c.Name,
                    Code = c.Code,
                    Ects = c.Ects,
                    Topics = c.Topics.Split(',').ToList(),
                })
                .ToList();
            quotas = new Dictionary<string, int>();
        }

        return new Inputs(
            new DashboardSummaryInput
            {
                Settings = settings,
                AllCourses = courses,
                Sessions = sessions,
                History = SummaryInputLoader.SliceHistory(sessions, now, DashboardSummaryBuilder.HistoryDays, onlyCompleted: false),
                HeavyHistory = SummaryInputLoader.SliceHistory(sessions, now, DashboardSummaryBuilder.AchievementHistoryDays),
                Goals = goals,
                GroupQuotas = quotas,
                Notes = notes,
                Now = now,
            },
            new StatsSummaryInput
            {
                Settings = settings,
                AllCourses = courses,
                Sessions = sessions,
                History = SummaryInputLoader.SliceHistory(sessions, now, StatsSummaryBuilder.HistoryDays),
                HeavyHistory = SummaryInputLoader.SliceHistory(sessions, now, StatsSummaryBuilder.AllTimeHistoryDays),
                Goals = goals,
                GroupQuotas = quotas,
                Notes = notes,
                Now = now,
            });
    }
}
