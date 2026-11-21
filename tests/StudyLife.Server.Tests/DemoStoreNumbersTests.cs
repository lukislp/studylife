// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SampleData;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// What the App Store screenshots of the demo show (Demo:Clock 09:41, both demo languages),
/// computed by the same shared builders the dashboard and stats pages use:
/// the week and month quota tiles are on track (no "below target" badge), the semester comparison
/// has a previous semester with hours, a grade and ECTS, and the German and English datasets
/// show the same numbers.
/// </summary>
public class DemoStoreNumbersTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DemoStoreNumbersTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private sealed record Seeded(DashboardSummaryInput Dashboard, StatsSummaryInput Stats);

    /// <summary>Reseeds the demo for <paramref name="now"/> and assembles the builder inputs from
    /// the database the way the summary endpoints do (all sessions are the demo user's).</summary>
    private async Task<Seeded> SeedAsync(DateTime now, SampleDataLanguage language)
    {
        await _factory.WithDbAsync(db => DemoSeeder.ReseedAsync(db, now, language));
        return await _factory.WithDbAsync(async db =>
        {
            var settingsEntity = await db.Settings.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            var settings = SettingsService.ToDto(settingsEntity);
            var sessions = (await db.Sessions.IgnoreQueryFilters().AsNoTracking().ToListAsync())
                .Select(SessionService.ToDto).ToList();
            var goals = (await db.CourseGoals.IgnoreQueryFilters().AsNoTracking().ToListAsync())
                .Select(CourseGoalService.ToDto).ToList();
            var notes = (await db.Notes.IgnoreQueryFilters().AsNoTracking().ToListAsync())
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
                courses = (await db.CustomCourses.IgnoreQueryFilters().AsNoTracking().ToListAsync())
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

            var history = SummaryInputLoader.SliceHistory(sessions, now, DashboardSummaryBuilder.HistoryDays, onlyCompleted: false);
            var heavy = SummaryInputLoader.SliceHistory(sessions, now, DashboardSummaryBuilder.AchievementHistoryDays);
            return new Seeded(
                new DashboardSummaryInput
                {
                    Settings = settings,
                    AllCourses = courses,
                    Sessions = sessions,
                    History = history,
                    HeavyHistory = heavy,
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
        });
    }

    public static TheoryData<string, SampleDataLanguage> Days()
    {
        // Three weeks across a month boundary (the hardest days: a Monday morning, the 1st of a
        // month) plus the day the v3.39.12 screenshots were taken.
        var data = new TheoryData<string, SampleDataLanguage>();
        var days = Enumerable.Range(0, 21).Select(i => new DateTime(2026, 11, 23).AddDays(i)).Append(new DateTime(2026, 11, 20));
        foreach (var day in days)
            foreach (var language in new[] { SampleDataLanguage.German, SampleDataLanguage.English })
                data.Add(day.ToString("yyyy-MM-dd"), language);
        return data;
    }

    [Theory]
    [MemberData(nameof(Days))]
    public async Task The_demo_week_and_month_quotas_are_on_track_at_the_screenshot_clock(string day, SampleDataLanguage language)
    {
        var now = DateTime.Parse(day).Add(new TimeSpan(9, 41, 0));
        var seeded = await SeedAsync(now, language);

        var sessions = DashboardSummaryBuilder.BuildSessions(seeded.Dashboard);

        Assert.False(sessions.WeekQuota.Warning, $"week {sessions.WeekQuota.HoursLabel} below {sessions.WeekQuota.TargetMin}h");
        Assert.False(sessions.MonthQuota.Warning, $"month {sessions.MonthQuota.HoursLabel} below {sessions.MonthQuota.TargetMin}h");
        Assert.Equal(SampleStudyGoals.Demo.WeeklyMinHours, sessions.WeekQuota.TargetMin);
        Assert.Equal(SampleStudyGoals.Demo.MonthlyMaxHours, sessions.MonthQuota.TargetMax);
    }

    [Fact]
    public async Task The_goal_plan_only_adds_future_evening_sessions_on_free_days()
    {
        var now = new DateTime(2026, 11, 30, 9, 41, 0); // a Monday: the plan has work to do
        await SeedAsync(now, SampleDataLanguage.German);
        var planned = await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => !s.IsCompleted).ToListAsync());

        Assert.True(planned.Count > 5, "the goal plan adds sessions beyond the fixed five");
        Assert.All(planned, s => Assert.True(s.StartTime > now));
        // One planned session per day at most: the plan only uses days without any session.
        Assert.Equal(planned.Count, planned.Select(s => s.StartTime.Date).Distinct().Count());
        Assert.All(planned.Where(s => s.StartTime.TimeOfDay == SampleDataset.GoalPlanStart),
            s => Assert.Equal(SampleDataset.PlannedLength, s.EndTime - s.StartTime));
    }

    [Theory]
    [InlineData(SampleDataLanguage.German)]
    [InlineData(SampleDataLanguage.English)]
    public async Task The_semester_comparison_has_a_previous_semester_with_hours_grade_and_ects(SampleDataLanguage language)
    {
        var seeded = await SeedAsync(new DateTime(2026, 11, 20, 9, 41, 0), language);

        var comparison = StatsSummaryBuilder.Build(seeded.Stats).Extended.SemesterComparison;

        Assert.True(comparison.HasData);
        Assert.Equal(2, comparison.CurrentSemester);
        Assert.True(comparison.PreviousHours > 50, $"previous semester only {comparison.PreviousHours}h");
        Assert.NotNull(comparison.PreviousGrade);
        Assert.Equal(25, comparison.PreviousEcts);
        Assert.True(comparison.CurrentHours > 0);
    }

    [Fact]
    public async Task The_german_and_english_demo_show_the_same_numbers()
    {
        var now = new DateTime(2026, 11, 20, 9, 41, 0);
        var german = await SeedAsync(now, SampleDataLanguage.German);
        var english = await SeedAsync(now, SampleDataLanguage.English);

        var de = DashboardSummaryBuilder.Build(german.Dashboard);
        var en = DashboardSummaryBuilder.Build(english.Dashboard);
        Assert.Equal(25, de.Goals.EctsEarned);
        Assert.Equal(180, de.Goals.EctsTotal);
        Assert.Equal(de.Goals.EctsEarned, en.Goals.EctsEarned);
        Assert.Equal(de.Goals.EctsTotal, en.Goals.EctsTotal);
        Assert.Equal(de.Goals.AverageGradeLabel, en.Goals.AverageGradeLabel);
        Assert.Equal(de.Progress.Forecast.DateLabel, en.Progress.Forecast.DateLabel);
        Assert.Equal(de.Sessions.WeekQuota.HoursLabel, en.Sessions.WeekQuota.HoursLabel);
        Assert.Equal(de.Sessions.MonthQuota.HoursLabel, en.Sessions.MonthQuota.HoursLabel);

        var deStats = StatsSummaryBuilder.Build(german.Stats);
        var enStats = StatsSummaryBuilder.Build(english.Stats);
        Assert.Equal(deStats.Core.TotalHoursLabel, enStats.Core.TotalHoursLabel);
        Assert.Equal(deStats.Core.Forecast.DateLabel, enStats.Core.Forecast.DateLabel);
        Assert.Equal(deStats.Extended.SemesterComparison.PreviousHours, enStats.Extended.SemesterComparison.PreviousHours);
        Assert.Equal(deStats.Extended.SemesterComparison.PreviousEcts, enStats.Extended.SemesterComparison.PreviousEcts);
    }
}
