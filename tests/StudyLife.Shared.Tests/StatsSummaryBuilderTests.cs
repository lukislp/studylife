using System.Globalization;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Golden parity test for the statistics-page extraction: every expected value below was derived
/// by hand-tracing the ORIGINAL client code (Stats.razor.cs plus the Stats.Charts/Trends/
/// Comparisons/Grades/Programs partials, before the computations moved into
/// <see cref="StatsSummaryBuilder"/>) against this fixture. It therefore pins what the statistics
/// page showed before the refactor, not what the builder happens to produce.
///
/// Fixture: Friday 2026-09-04 14:30, five courses of the active programme (two completed, one of
/// them in an elective group with a quota) plus a second programme with two courses of its own,
/// 49 sessions spread over 18 months - completed and open, past, running, later today, in the
/// future, and three deliberately older than the 371-day window so the all-time history sees
/// something the 12-month one does not - five course goals, eight notes and two study programmes.
///
/// The native cardio-fitness card is deliberately absent here, exactly as it is from the DTO:
/// health data never leaves the device (Stats.Health.razor.cs).
/// </summary>
public class StatsSummaryBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 14, 30, 0);

    private const int ActiveProgramId = 1;
    private const int OtherProgramId = 2;

    private static List<CourseDto> Courses() => new()
    {
        new() { Id = 1, Semester = 1, Name = "Artificial Intelligence", Code = "AI-101", Color = "#6C5CE7", Icon = "*", Ects = 5, Topics = new() { "T1", "T2", "T3" } },
        new() { Id = 2, Semester = 1, Name = "Python", Code = "PY-101", Color = "#00B894", Icon = "P", Ects = 5, Topics = new() { "A", "B" } },
        new() { Id = 3, Semester = 2, Name = "Statistik", Code = "ST-201", Color = "#0984E3", Icon = "S", Ects = 10 },
        new() { Id = 4, Semester = 2, Name = "Datenbanken", Code = "DB-201", Color = "#E17055", Icon = "D", Ects = 5, Topics = new() { "D1", "D2" } },
        new() { Id = 5, Semester = 2, Name = "Wahlfach Ethik", Code = "WE-301", Color = "#FDCB6E", Icon = "E", Ects = 5, Group = "Wahlmodule" },
    };

    /// <summary>The second programme's catalog - only the cross-programme comparison sees it.</summary>
    private static List<CourseDto> OtherProgramCourses() => new()
    {
        new() { Id = 50, Semester = 1, Name = "Marketing", Code = "MK-101", Color = "#D63031", Icon = "M", Ects = 5 },
        new() { Id = 51, Semester = 1, Name = "Recht", Code = "RE-101", Color = "#636E72", Icon = "R", Ects = 5 },
    };

    private static UserSettingsDto Settings() => new()
    {
        SelectedCourseIds = new() { 1, 2, 3, 4, 5 },
        CompletedCourseIds = new() { 1, 3 },
        WeeklyGoalMinHours = 25,
        WeeklyGoalMaxHours = 30,
        MonthlyGoalMinHours = 100,
        MonthlyGoalMaxHours = 130,
        InactivityThresholdDays = 5,
        StudyDays = "0,1,2,3,4,5,6",
        StudyWindowStartHour = 8,
        StudyWindowEndHour = 21,
        TargetGraduationDate = new DateTime(2027, 9, 30),
        ActiveStudyProgramId = ActiveProgramId,
    };

    /// <summary>Ids are the 1-based position in this list, so the assertions can name sessions.</summary>
    private static List<StudySessionDto> AllSessions()
    {
        (int Course, string Start, double Hours, bool Completed)[] raw =
        {
            // Older than the 371-day window - only the all-time history sees these.
            (1, "2025-03-11 09:00", 3, true),
            (2, "2025-05-20 18:00", 2, true),
            (3, "2025-07-02 08:00", 4, true),
            // Inside the 371-day window, before the 12-week charts.
            (1, "2025-10-14 09:00", 2, true),
            (2, "2025-12-03 20:00", 1.5, true),
            (1, "2026-01-20 07:00", 2, true),
            (3, "2026-02-17 14:00", 3, true),
            (2, "2026-03-24 09:00", 2.5, true),
            (1, "2026-04-14 10:00", 1, true),
            (3, "2026-05-19 16:00", 2, true),
            (4, "2026-06-09 09:00", 3, true),
            (2, "2026-06-23 21:00", 1, true),
            (4, "2026-07-07 13:00", 2, true),
            (1, "2026-07-21 09:00", 2, true),
            // The last ~13 weeks - the 12-week grid of the weekly charts starts 2026-06-15.
            (2, "2026-06-16 09:00", 2, true),
            (3, "2026-06-30 09:00", 2, true),
            (4, "2026-07-14 11:00", 1.5, true),
            (2, "2026-07-28 09:00", 3, true),
            (3, "2026-08-04 14:00", 2, true),
            (2, "2026-08-05 19:00", 3, true),
            (1, "2026-08-06 09:00", 2, true),
            (4, "2026-08-07 09:00", 1, false),   // past, not timer-completed -> studied by elapsed time
            (2, "2026-08-10 09:00", 3, true),
            (3, "2026-08-11 09:00", 2, true),
            (2, "2026-08-12 09:00", 2, true),
            (3, "2026-08-13 20:00", 2, true),
            (4, "2026-08-14 15:00", 2.5, true),
            (2, "2026-08-17 09:00", 3, true),
            (3, "2026-08-18 09:00", 3, true),
            (2, "2026-08-19 09:00", 2, true),
            (1, "2026-08-20 09:00", 2, true),
            (4, "2026-08-21 09:00", 1, true),
            (2, "2026-08-24 09:00", 3, true),
            (3, "2026-08-25 09:00", 2, true),
            (2, "2026-08-26 09:00", 2, true),
            (1, "2026-08-27 09:00", 2, true),
            (4, "2026-08-28 09:00", 1.5, true),
            (2, "2026-08-31 09:00", 2, true),
            (3, "2026-09-01 09:00", 1, true),
            (2, "2026-09-02 09:00", 1, true),
            (4, "2026-09-02 15:00", 2, true),
            (1, "2026-09-03 09:00", 1, true),
            (2, "2026-09-04 09:00", 1.5, true),
            (3, "2026-09-04 14:00", 1, false),   // running right now
            (1, "2026-09-04 18:00", 2, false),   // later today
            (2, "2026-09-06 10:00", 2, false),   // future
            // Another programme's courses - only the cross-programme comparison sees these.
            (50, "2026-08-18 09:00", 4, true),
            (50, "2026-08-25 09:00", 2, true),
            (51, "2026-09-01 09:00", 3, true),
        };

        var courses = Courses().Concat(OtherProgramCourses()).ToList();
        var sessions = new List<StudySessionDto>();
        for (var i = 0; i < raw.Length; i++)
        {
            var start = DateTime.ParseExact(raw[i].Start, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var course = courses.FirstOrDefault(c => c.Id == raw[i].Course);
            sessions.Add(new StudySessionDto
            {
                Id = i + 1,
                CourseId = raw[i].Course,
                CourseName = course?.Name ?? "",
                CourseColor = course?.Color ?? "#123456",
                StartTime = start,
                EndTime = start.AddHours(raw[i].Hours),
                IsCompleted = raw[i].Completed,
            });
        }
        return sessions;
    }

    private static List<CourseGoalDto> Goals() => new()
    {
        new() { CourseId = 1, CourseName = "Artificial Intelligence", TargetDate = new DateTime(2026, 6, 30), CompletedAt = new DateTime(2026, 7, 1), Grade = 1.7m, CompletedTopics = "T1,T2,T3", Tag = "done" },
        new() { CourseId = 2, CourseName = "Python", TargetDate = new DateTime(2026, 9, 20), Grade = 2.3m, CompletedTopics = "A", Tag = "exam" },
        new() { CourseId = 3, CourseName = "Statistik", TargetDate = new DateTime(2026, 8, 10), CompletedAt = new DateTime(2026, 8, 15), Grade = 2.0m, CompletedTopics = "" },
        new() { CourseId = 4, CourseName = "Datenbanken", TargetDate = new DateTime(2026, 12, 15), CompletedTopics = "D1" },
        // Another programme's goal - must never reach the average grade or the grade charts.
        new() { CourseId = 50, CourseName = "Marketing", CompletedAt = new DateTime(2026, 5, 1), Grade = 3.0m },
    };

    private static List<NoteDto> Notes() => new()
    {
        new() { Id = 1, Title = "A", Content = "a", CourseId = 2, CreatedAt = new DateTime(2026, 6, 24, 8, 0, 0), UpdatedAt = new DateTime(2026, 6, 24, 8, 0, 0) },
        new() { Id = 2, Title = "B", Content = "b", CourseId = null, CreatedAt = new DateTime(2026, 7, 15, 8, 0, 0), UpdatedAt = new DateTime(2026, 7, 15, 8, 0, 0) },
        new() { Id = 3, Title = "C", Content = "c", CourseId = 3, CreatedAt = new DateTime(2026, 8, 5, 8, 0, 0), UpdatedAt = new DateTime(2026, 8, 5, 8, 0, 0) },
        new() { Id = 4, Title = "D", Content = "d", CourseId = 2, CreatedAt = new DateTime(2026, 8, 6, 8, 0, 0), UpdatedAt = new DateTime(2026, 8, 6, 8, 0, 0) },
        new() { Id = 5, Title = "E", Content = "e", CourseId = null, CreatedAt = new DateTime(2026, 8, 20, 8, 0, 0), UpdatedAt = new DateTime(2026, 8, 20, 8, 0, 0) },
        new() { Id = 6, Title = "F", Content = "f", CourseId = 4, CreatedAt = new DateTime(2026, 9, 2, 8, 0, 0), UpdatedAt = new DateTime(2026, 9, 2, 8, 0, 0) },
        // Another programme's note (dropped by the scope filter) and one outside the 12-week
        // window (kept by the filter, but in no bucket).
        new() { Id = 7, Title = "G", Content = "g", CourseId = 50, CreatedAt = new DateTime(2026, 9, 3, 8, 0, 0), UpdatedAt = new DateTime(2026, 9, 3, 8, 0, 0) },
        new() { Id = 8, Title = "H", Content = "h", CourseId = 2, CreatedAt = new DateTime(2025, 1, 5, 8, 0, 0), UpdatedAt = new DateTime(2025, 1, 5, 8, 0, 0) },
    };

    private static List<StudyProgramSummaryDto> StudyPrograms() => new()
    {
        new() { Id = ActiveProgramId, Name = "Angewandte KI" },
        new() { Id = OtherProgramId, Name = "BWL", IsCompleted = true },
    };

    private static List<StatsProgramCatalogDto> ProgramCatalogs() => new()
    {
        new() { ProgramId = ActiveProgramId, Courses = Courses(), GroupQuotas = new() { ["Wahlmodule"] = 5 } },
        new() { ProgramId = OtherProgramId, Courses = OtherProgramCourses(), GroupQuotas = new() },
    };

    private static StatsSummaryInput Input()
    {
        var all = AllSessions();
        return new StatsSummaryInput
        {
            Settings = Settings(),
            AllCourses = Courses(),
            // The three fetch windows the page uses, applied to the same master list.
            Sessions = all.Where(s => s.StartTime >= Now.AddDays(-7) && s.StartTime <= Now.AddDays(90)).ToList(),
            History = all.Where(s => s.StartTime >= Now.AddDays(-StatsSummaryBuilder.HistoryDays)).ToList(),
            HeavyHistory = all.Where(s => s.StartTime >= Now.AddDays(-StatsSummaryBuilder.AllTimeHistoryDays) && StudyMetrics.IsStudied(s, Now)).ToList(),
            Goals = Goals(),
            GroupQuotas = new Dictionary<string, int> { ["Wahlmodule"] = 5 },
            StudyPrograms = StudyPrograms(),
            ProgramCatalogs = ProgramCatalogs(),
            Notes = Notes(),
            Now = Now,
        };
    }

    private static StatsCoreSummaryDto Core() => StatsSummaryBuilder.Build(Input()).Core;

    // ── Course list + summary tiles ───────────────────────────────────────────

    [Fact]
    public void CourseRows_AreOrderedByHoursAndScopedToTheActiveProgramme()
    {
        var rows = Core().CourseRows;

        // Rows now come from the FULL (studied) history instead of the ±7/90-day session window,
        // so every studied session of the fixture counts: course 2 = 34.5h/16, course 3 = 23h/10,
        // course 1 = 17h/9, course 4 = 14.5h/8 (the three not-yet-studied sessions of 04./06.09.
        // are excluded). Course 5 still has no sessions at all and stays absent.
        // Before: only the near-term window -> 2 = 4.5h/3, 4 = 2h/1, 1 = 1h/1, 3 = 1h/1.
        Assert.Equal(new[] { 2, 3, 1, 4 }, rows.Select(r => r.Course.Id));
        Assert.Equal(new[] { 34.5, 23.0, 17.0, 14.5 }, rows.Select(r => r.Hours));
        Assert.Equal(new[] { 16, 10, 9, 8 }, rows.Select(r => r.SessionCount));
        Assert.Equal(new[] { false, true, true, false }, rows.Select(r => r.IsCompleted));
        // Completed courses never show a remaining deadline, even with a target date on the goal.
        Assert.Equal(new int?[] { 16, null, null, 102 }, rows.Select(r => r.DaysRemaining));
        Assert.Equal(new decimal?[] { 2.3m, 2.0m, 1.7m, null }, rows.Select(r => r.Grade));
        // Bars scale to the new maximum (34.5h): 23/34.5, 17/34.5, 14.5/34.5.
        Assert.Equal(100.0, rows[0].BarPercent, 6);
        Assert.Equal(66.666667, rows[1].BarPercent, 5);
        Assert.Equal(49.275362, rows[2].BarPercent, 5);
        Assert.Equal(42.028986, rows[3].BarPercent, 5);
        // Ring: completed = 100, otherwise the topic checklist share (1 of 2 topics each).
        Assert.Equal(new[] { 50.0, 100.0, 100.0, 50.0 }, rows.Select(r => r.RingPercent));
        Assert.Equal(new[] { 0, 10, 5, 0 }, rows.Select(r => r.EctsEarned));
    }

    [Fact]
    public void CourseRows_CarryTheThirtyDayTrendAndTheTwelveWeekSparkline()
    {
        var rows = Core().CourseRows;

        // Same per-course trend values as before, only re-ordered with the rows (2, 3, 1, 4):
        // the 30-day windows themselves are unchanged.
        Assert.Equal(225.0, rows[0].TrendPercent!.Value, 6);
        Assert.Equal(400.0, rows[1].TrendPercent!.Value, 6);
        Assert.Equal(250.0, rows[2].TrendPercent!.Value, 6);
        Assert.Equal(128.571429, rows[3].TrendPercent!.Value, 5);

        // Twelve weekly buckets, normalized to the course's own strongest week.
        Assert.Equal(new[] { 40.0, 20, 0, 0, 0, 0, 60, 60, 100, 100, 100, 90 }, rows[0].Spark!);
    }

    [Fact]
    public void SummaryTiles_MatchTheOriginalLabelsAndEcts()
    {
        var core = Core();

        // Totals follow the course rows: 34.5 + 23 + 17 + 14.5 = 89h over 16+10+9+8 = 43 sessions
        // (was "8h 30m" / 6, the near-term window's share of the same history).
        Assert.Equal("89h 0m", core.TotalHoursLabel);
        Assert.Equal(43, core.TotalSessions);
        // ECTS-weighted over the active programme's graded goals only (the other programme's 3.0
        // must not pull this down).
        Assert.Equal("2,00", core.AverageGradeLabel);

        // The elective group's quota (5) caps its single 5-ECTS course; the rest counts in full.
        Assert.Equal(30, core.EctsTotal);
        Assert.Equal(15, core.EctsEarned);
        Assert.Equal(50.0, core.EctsPercent, 6);
    }

    [Fact]
    public void Forecast_MatchesTheOriginalDate()
    {
        var forecast = Core().Forecast;

        Assert.True(forecast.Available);
        Assert.False(forecast.AlreadyDone);
        Assert.Equal("19.08.2028", forecast.DateLabel);
    }

    [Fact]
    public void MonthComparison_IsThisMonthAgainstLastMonth()
    {
        var comparison = Core().MonthComparison;

        // studied-only: September so far is 6.5h, not 11.5h (the 1h running session, the 2h at
        // 18:00 and Sunday's 2h have not been studied), against the whole of August (43h)
        // -> 36h 30m behind instead of 31h 30m.
        Assert.False(comparison.Up);
        Assert.Equal("36h 30m", comparison.DeltaLabel);
    }

    // ── Grades ────────────────────────────────────────────────────────────────

    [Fact]
    public void GradeDistribution_CountsEveryGradeOfTheActiveProgramme()
    {
        var buckets = Core().GradeDistribution;

        Assert.Equal(new[] { "1,0–1,5", "1,6–2,0", "2,1–2,5", "2,6–3,0", "3,1–3,5", "3,6–4,0", "> 4,0" },
            buckets.Select(b => b.Label));
        Assert.Equal(new[] { 0, 2, 1, 0, 0, 0, 0 }, buckets.Select(b => b.Count));
        Assert.Equal(new[] { 0.0, 100, 50, 0, 0, 0, 0 }, buckets.Select(b => b.Percent));
    }

    [Fact]
    public void GradeHistory_AveragesPerSemesterWithTheInvertedBar()
    {
        var points = Core().GradeHistory;

        Assert.Equal(new[] { 1, 2 }, points.Select(p => p.Semester));
        Assert.Equal(new[] { 2.0m, 2.0m }, points.Select(p => p.AvgGrade));
        Assert.Equal(new[] { 2, 1 }, points.Select(p => p.CourseCount));
        Assert.Equal(new[] { 75.0, 75.0 }, points.Select(p => p.BarPercent));
    }

    [Fact]
    public void GradeTimeline_IsChronologicalAndNeedsNoSemester()
    {
        var points = Core().GradeTimeline;

        Assert.Equal(new[] { new DateTime(2026, 7, 1), new DateTime(2026, 8, 15) }, points.Select(p => p.Date));
        Assert.Equal(new[] { "Artificial Intelligence", "Statistik" }, points.Select(p => p.CourseName));
        Assert.Equal(new[] { "#6C5CE7", "#0984E3" }, points.Select(p => p.Color));
        Assert.Equal(new[] { 1.7m, 2.0m }, points.Select(p => p.Grade));
        Assert.Equal(new[] { 82.5, 75.0 }, points.Select(p => p.BarPercent));
    }

    [Fact]
    public void HoursGradeScatter_PlacesEveryGradedCourseWithTheFivePercentMargin()
    {
        var scatter = Core().HoursGradeScatter;

        // Hours come from the same per-course aggregate as the course rows, so they now span the
        // full history: 17 / 34.5 / 23 instead of 1 / 4.5 / 1. Scale = ceil(34.5) = 35h.
        Assert.Equal("35h", scatter.MaxHoursLabel);
        Assert.Equal(new[] { "Artificial Intelligence", "Python", "Statistik" }, scatter.Points.Select(p => p.Name));
        Assert.Equal(new[] { 17.0, 34.5, 23.0 }, scatter.Points.Select(p => p.Hours));
        // 5 + h/35 * 90.
        Assert.Equal(new[] { 48.714286, 93.714286, 64.142857 }, scatter.Points.Select(p => Math.Round(p.XPercent, 6)));
        // The grade axis is untouched.
        Assert.Equal(new[] { 79.25, 65.75, 72.5 }, scatter.Points.Select(p => p.YPercent));
    }

    [Fact]
    public void HoursEctsScatter_AlsoIncludesCompletedCoursesWithoutLoggedHours()
    {
        var scatter = Core().HoursEctsScatter;

        // Same full-history hours as the hours-vs-grade scatter (17 / 34.5 / 23 / 14.5), so the
        // x-axis scale is ceil(34.5) = 35h here too; the ECTS axis is untouched.
        Assert.Equal("35h", scatter.MaxHoursLabel);
        Assert.Equal("10", scatter.MaxEctsLabel);
        Assert.Equal(new[] { "Artificial Intelligence", "Python", "Statistik", "Datenbanken" }, scatter.Points.Select(p => p.Name));
        Assert.Equal(new[] { 5, 0, 10, 0 }, scatter.Points.Select(p => p.EctsEarned));
        Assert.Equal(new[] { 48.714286, 93.714286, 64.142857, 42.285714 }, scatter.Points.Select(p => Math.Round(p.XPercent, 6)));
        Assert.Equal(new[] { 50.0, 5.0, 95.0, 5.0 }, scatter.Points.Select(p => p.YPercent));
    }

    // ── Charts ────────────────────────────────────────────────────────────────

    [Fact]
    public void Heatmap_Is53WeeksEndingInTheCurrentOne()
    {
        var weeks = Core().Heatmap.Weeks;

        Assert.Equal(53, weeks.Count);
        Assert.Equal(new DateTime(2025, 9, 1), weeks[0].WeekStart);
        Assert.Equal(new DateTime(2026, 8, 31), weeks[52].WeekStart);
        Assert.All(weeks, w => Assert.Equal(7, w.Days.Count));

        // A month label is emitted only on the first week of each new month.
        Assert.Equal(new[] { 0, 5, 9, 13, 18, 22, 26, 31, 35, 39, 44, 48 },
            weeks.Select((w, i) => (w, i)).Where(x => x.w.ShowMonthLabel).Select(x => x.i));

        // Days after today stay at level -1 (empty placeholder), even with a session on them.
        var future = weeks[52].Days.Where(d => d.Level == -1).ToList();
        Assert.Equal(new[] { new DateTime(2026, 9, 5), new DateTime(2026, 9, 6) }, future.Select(d => d.Date));
        Assert.All(future, d => Assert.Empty(d.Courses));
    }

    [Fact]
    public void Heatmap_TodaysCellCarriesHoursLevelAndThePerCourseBreakdown()
    {
        var today = Core().Heatmap.Weeks.SelectMany(w => w.Days).Single(d => d.Date == new DateTime(2026, 9, 4));

        // studied-only: of today's three sessions only the 1.5h one (09:00, completed) has been
        // studied - the 14:00 session still runs and the 18:00 one hasn't started. 4.5h/level 4/3
        // sessions before; 1.5h now, which lands in the "<= 2h" band (level 2).
        Assert.Equal(1.5, today.Hours, 6);
        Assert.Equal(2, today.Level);
        Assert.Equal(1, today.SessionCount);
        // Hours descending; colors resolved here, the display names stay on the client.
        Assert.Equal(new[] { 2 }, today.Courses.Select(c => c.CourseId));
        Assert.Equal(new[] { 1.5 }, today.Courses.Select(c => c.Hours));
        Assert.Equal(new[] { "#00B894" }, today.Courses.Select(c => c.Color));
    }

    [Fact]
    public void Donut_RanksCoursesByHoursOverTheWholeHistoryWindow()
    {
        var donut = Core().Donut;

        // studied-only removes the three sessions of 04./06.09.: course 2 loses 2h (34.5 -> 32.5),
        // course 3 loses 1h (20 -> 19), course 1 loses 2h (16 -> 14); course 4 is untouched at
        // 14.5 and therefore overtakes course 1. Total 85 -> 80.
        Assert.Equal(80.0, donut.TotalHours, 6);
        Assert.Equal(new[] { 2, 3, 4, 1 }, donut.Slices.Select(s => s.CourseId));
        Assert.Equal(new[] { 32.5, 19.0, 14.5, 14.0 }, donut.Slices.Select(s => s.Hours));
        Assert.Equal(new[] { 15, 9, 8, 8 }, donut.Slices.Select(s => s.SessionCount));
        Assert.Equal(40.625, donut.Slices[0].Percent, 6); // 32.5 / 80
        Assert.Equal("conic-gradient(#00B894 0% 40.625%, #0984E3 40.625% 64.375%, #E17055 64.375% 82.5%, #6C5CE7 82.5% 100%)",
            donut.Gradient);
    }

    [Fact]
    public void Donut_EmbedsTheTwelveMonthDrilldownAndTheEightNewestSessions()
    {
        var python = Core().Donut.Slices[0];

        Assert.Equal(12, python.Months.Count);
        Assert.Equal(new DateTime(2025, 10, 1), python.Months[0].MonthStart);
        Assert.Equal(new DateTime(2026, 9, 1), python.Months[11].MonthStart);
        // studied-only: September loses the 2h session of Sunday 06.09. -> 4.5 becomes 2.5.
        Assert.Equal(new[] { 0.0, 0, 1.5, 0, 0, 2.5, 0, 0, 3, 3, 20, 2.5 }, python.Months.Select(m => m.Hours));
        // Scaled against this course's own strongest month (August, 20h).
        Assert.Equal(new[] { 0.0, 0, 7.5, 0, 0, 12.5, 0, 0, 15, 15, 100, 12.5 }, python.Months.Select(m => m.Percent));

        // The newest entry is no longer Sunday's planned session, so the whole window shifts by
        // one: it starts at 04.09. and reaches back to 12.08. instead of 17.08.
        Assert.Equal(8, python.RecentSessions.Count);
        Assert.Equal(new DateTime(2026, 9, 4, 9, 0, 0), python.RecentSessions[0].Start);
        Assert.Equal(new DateTime(2026, 8, 12, 9, 0, 0), python.RecentSessions[7].Start);
    }

    [Fact]
    public void Rhythm_SumsHoursPerWeekdayAndPerTimeOfDayBucket()
    {
        var rhythm = Core().Rhythm;

        // 0 = Monday .. 6 = Sunday; raw hours, the localized names stay on the client.
        // studied-only: Friday loses today's 1h + 2h (10.5 -> 7.5) and Sunday its planned 2h
        // (2 -> 0). Monday..Thursday are unchanged, so the maximum stays Tuesday's 39h.
        Assert.Equal(new[] { 11.0, 39.0, 13.5, 9.0, 7.5, 0.0, 0.0 }, rhythm.WeekdayHours);
        Assert.Equal(39.0, rhythm.WeekdayMax, 6);

        Assert.Equal(new[] { "00-06", "06-09", "09-12", "12-15", "15-18", "18-21", "21-24" },
            rhythm.TimeOfDay.Select(b => b.Label));
        // Same three sessions by start hour: 10:00 -> 09-12 (59 -> 57), 14:00 -> 12-15 (8 -> 7),
        // 18:00 -> 18-21 (8.5 -> 6.5).
        Assert.Equal(new[] { 0.0, 2.0, 57.0, 7.0, 6.5, 6.5, 1.0 }, rhythm.TimeOfDay.Select(b => b.Hours));
        Assert.Equal(100.0, rhythm.TimeOfDay[2].Percent, 6);
        Assert.Equal(3.508772, rhythm.TimeOfDay[1].Percent, 5); // 2 / 57
    }

    [Fact]
    public void TimeHeatmap_BucketsEverySessionIntoItsWeekdayAndStartHour()
    {
        var grid = Core().TimeHeatmap;

        Assert.Equal(7, grid.HoursByCell.Count);
        Assert.All(grid.HoursByCell, row => Assert.Equal(24, row.Count));
        // Tuesday 09:00 is the busiest slot and therefore the grid's own scale.
        Assert.Equal(24.5, grid.MaxCell, 6);
        Assert.Equal(24.5, grid.HoursByCell[1][9], 6);
        Assert.Equal(11, grid.SessionCountByCell[1][9]);
        Assert.Equal(11.0, grid.HoursByCell[0][9], 6);
        // Saturday stays completely empty in this fixture.
        Assert.All(grid.HoursByCell[5], h => Assert.Equal(0.0, h));

        var busiest = grid.CellCourses.Single(c => c.Weekday == 1 && c.Hour == 9);
        Assert.Equal(new[] { 3, 2, 1, 4 }, busiest.Courses.Select(c => c.CourseId));
        Assert.Equal(new[] { 10.0, 7.5, 4.0, 3.0 }, busiest.Courses.Select(c => c.Hours));
    }

    [Fact]
    public void MonthlyBreakdown_CarriesSixMonthsOfRawPerCourseHours()
    {
        var monthly = Core().MonthlyBreakdown;

        Assert.Equal(6, monthly.MonthStarts.Count);
        Assert.Equal(new DateTime(2026, 4, 1), monthly.MonthStarts[0]);
        Assert.Equal(new DateTime(2026, 9, 1), monthly.MonthStarts[5]);
        // Stacking order: total hours over the whole window, descending.
        Assert.Equal(new[] { 2, 3, 4, 1 }, monthly.OrderedIds);
        // Fewer than six courses, so nothing collapses into the "other" slice.
        Assert.Equal(monthly.OrderedIds, monthly.TopIds);
        Assert.Equal(43.0, monthly.MaxMonthTotal, 6);

        // studied-only: only September moves, losing the 1h + 2h + 2h that are not studied yet.
        Assert.Equal(new[] { 1.0, 2.0, 8.0, 8.5, 43.0, 6.5 },
            monthly.PerMonthCourseHours.Select(d => d.Values.Sum()));
        var august = monthly.PerMonthCourseHours[4];
        Assert.Equal(20.0, august[2], 6);
        Assert.Equal(11.0, august[3], 6);
        Assert.Equal(6.0, august[4], 6);
        Assert.Equal(6.0, august[1], 6);
    }

    // ── Trends ────────────────────────────────────────────────────────────────

    [Fact]
    public void EctsTimeline_AccumulatesCompletedGoalsOnly()
    {
        var points = Core().EctsTimeline;

        Assert.Equal(new[] { new DateTime(2026, 7, 1), new DateTime(2026, 8, 15) }, points.Select(p => p.Date));
        Assert.Equal(new[] { 5, 15 }, points.Select(p => p.CumulativeEcts));
        Assert.Equal(33.333333, points[0].Percent, 5);
        Assert.Equal(100.0, points[1].Percent, 6);
    }

    [Fact]
    public void EctsPlan_RunsMonthlyFromTheFirstCompletionToTheTargetDate()
    {
        var points = Core().EctsPlan;

        Assert.Equal(15, points.Count);
        Assert.Equal("07.26", points[0].Label);
        Assert.Equal("09.27", points[14].Label);
        // Actual stops at the current month; the target line reaches EctsTotal at the target date.
        Assert.Equal(new int?[] { 5, 15, 15 }, points.Take(3).Select(p => p.ActualEcts));
        Assert.All(points.Skip(3), p => Assert.Null(p.ActualEcts));
        Assert.Equal(new[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30 }, points.Select(p => p.TargetEcts));
        Assert.Equal(16.666667, points[0].ActualPercent!.Value, 5);
        Assert.Equal(100.0, points[14].TargetPercent, 6);
    }

    [Fact]
    public void ProductivityScore_IsTheTimerCompletedShareOfEachWeek()
    {
        var weeks = Core().ProductivityWeeks;

        Assert.Equal(new[] { "15.06.", "22.06.", "29.06.", "06.07.", "13.07.", "20.07.", "27.07.", "03.08.", "10.08.", "17.08.", "24.08.", "31.08." },
            weeks.Select(w => w.Label));
        // Only the week of 03.08. holds the one non-timer-completed session (3 of 4).
        Assert.Equal(new double?[] { 100, 100, 100, 100, 100, 100, 100, 75, 100, 100, 100, 100 },
            weeks.Select(w => w.Percent));
    }

    [Fact]
    public void GoalHistoryAndInactivityTrend_ShareTheSameTwelveWeekHours()
    {
        var core = Core();
        var expectedHours = new[] { 2.0, 1, 2, 2, 1.5, 2, 3, 8, 11.5, 11, 10.5, 8.5 };

        Assert.Equal(new DateTime(2026, 6, 15), core.GoalHistoryWeeks[0].WeekStart);
        Assert.Equal(new DateTime(2026, 8, 31), core.GoalHistoryWeeks[11].WeekStart);
        Assert.Equal(expectedHours, core.GoalHistoryWeeks.Select(w => w.Hours));
        // No week reaches the 25h weekly goal.
        Assert.All(core.GoalHistoryWeeks, w => Assert.False(w.Met));

        Assert.Equal(expectedHours, core.InactivityWeeks.Select(w => w.Hours));
        Assert.Equal(100.0, core.InactivityWeeks[8].Percent, 6);
        Assert.Equal(17.391304, core.InactivityWeeks[0].Percent, 5);
    }

    [Fact]
    public void SessionLengthHistogram_CountsStudiedSessionsPerLengthBand()
    {
        var buckets = Core().SessionLengthBuckets;

        Assert.Equal(new[] { "<30m", "30-60m", "60-90m", "90-120m", "120m+" }, buckets.Select(b => b.Label));
        Assert.Equal(new[] { 0, 0, 7, 4, 29 }, buckets.Select(b => b.Count));
        Assert.Equal(new[] { 0.0, 0.0, 24.137931, 13.793103, 100.0 }, buckets.Select(b => Math.Round(b.Percent, 6)));
    }

    // ── Comparisons ───────────────────────────────────────────────────────────

    [Fact]
    public void CourseComparison_TakesTheTopCoursesOnOneSharedScale()
    {
        var comparison = Core().CourseComparison;

        // Four courses studied in the window, so the top-5 cap doesn't bite here.
        Assert.Equal(new[] { 2, 3, 4, 1 }, comparison.TopCourseIds);
        Assert.Equal(12, comparison.WeekStarts.Count);
        Assert.Equal(new DateTime(2026, 6, 15), comparison.WeekStarts[0]);
        Assert.Equal(5.0, comparison.MaxHours, 6);
        // Every week holds exactly the top course ids, zeros included.
        Assert.All(comparison.PerWeekPerCourse, d => Assert.Equal(comparison.TopCourseIds.OrderBy(x => x), d.Keys.OrderBy(x => x)));
        Assert.Equal(5.0, comparison.PerWeekPerCourse[8][2], 6);
        Assert.Equal(4.0, comparison.PerWeekPerCourse[8][3], 6);
        Assert.Equal(0.0, comparison.PerWeekPerCourse[8][1], 6);
    }

    [Fact]
    public void CourseBalance_ComparesEctsShareAgainstTimeShareOfActiveCoursesOnly()
    {
        var rows = Core().CourseBalance;

        // Active = selected minus completed -> courses 2, 4, 5 (equal ECTS, so equal target).
        // Ordered by the largest absolute deviation.
        Assert.Equal(new[] { "Python", "Wahlfach Ethik", "Datenbanken" }, rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Equal(33.333333, r.TargetPercent, 5));
        Assert.Equal(69.148936, rows[0].ActualPercent, 5);
        Assert.Equal(0.0, rows[1].ActualPercent, 6);
        Assert.Equal(30.851064, rows[2].ActualPercent, 5);
    }

    [Fact]
    public void NotesCorrelation_PairsNoteCountsWithStudyHoursPerWeek()
    {
        var weeks = StatsSummaryBuilder.Build(Input()).Notes.CorrelationWeeks;

        Assert.Equal(12, weeks.Count);
        Assert.Equal("15.06.", weeks[0].Label);
        // Note 7 belongs to another programme and note 8 predates the window.
        Assert.Equal(new[] { 0, 1, 0, 0, 1, 0, 0, 2, 0, 1, 0, 1 }, weeks.Select(w => w.NotesCount));
        Assert.Equal(new[] { 0.0, 50, 0, 0, 50, 0, 0, 100, 0, 50, 0, 50 }, weeks.Select(w => w.NotesPercent));
        Assert.Equal(new[] { 2.0, 1, 2, 2, 1.5, 2, 3, 8, 11.5, 11, 10.5, 8.5 }, weeks.Select(w => w.Hours));
        Assert.Equal(100.0, weeks[8].HoursPercent, 6);
    }

    [Fact]
    public void NotesCorrelation_StaysEmptyBelowTheMinimumNoteCount()
    {
        var input = Input();
        input.Notes = Notes().Take(3).ToList();

        Assert.Empty(StatsSummaryBuilder.BuildNotes(input).CorrelationWeeks);
    }

    [Fact]
    public void SemesterComparison_PutsTheCurrentSemesterAgainstTheAverageOfThePreviousOnes()
    {
        var semester = StatsSummaryBuilder.Build(Input()).Extended.SemesterComparison;

        Assert.True(semester.HasData);
        // Most common semester among the active (selected, not completed) courses 2, 4, 5.
        Assert.Equal(2, semester.CurrentSemester);
        Assert.Equal(37.5, semester.CurrentHours, 6);
        Assert.Equal(2.00m, semester.CurrentGrade);
        Assert.Equal(10, semester.CurrentEcts);
        Assert.Equal(51.5, semester.PreviousHours, 6);
        Assert.Equal(2.0, semester.PreviousGrade!.Value, 6);
        Assert.Equal(5.0, semester.PreviousEcts, 6);
        Assert.Equal(51.5, semester.MaxHoursScale, 6);
        Assert.Equal(10.0, semester.MaxEctsScale, 6);
    }

    [Fact]
    public void SemesterComparison_ResolvesATieTowardTheMoreAdvancedSemester()
    {
        var input = Input();
        // One active course per semester (1, 2, 3) - the tie must fall to the highest.
        input.AllCourses = Courses();
        input.AllCourses.Single(c => c.Id == 5).Semester = 3;

        Assert.Equal(3, StatsSummaryBuilder.BuildExtended(input).SemesterComparison.CurrentSemester);
    }

    [Fact]
    public void ProgramComparison_ScoresEveryProgrammeOnItsOwnCatalog()
    {
        var rows = StatsSummaryBuilder.Build(Input()).Extended.ProgramComparison;

        Assert.Equal(new[] { "Angewandte KI", "BWL" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { true, false }, rows.Select(r => r.IsActive));
        Assert.Equal(new[] { false, true }, rows.Select(r => r.IsCompleted));
        Assert.Equal(new[] { 80.0, 9.0 }, rows.Select(r => r.Hours));
        Assert.Equal(new[] { 40, 3 }, rows.Select(r => r.SessionCount));
        Assert.Equal(new[] { 15, 0 }, rows.Select(r => r.EctsEarned));
        Assert.Equal(new[] { 30, 10 }, rows.Select(r => r.EctsTotal));
        Assert.Equal(new[] { "2,00", "3,00" }, rows.Select(r => r.GradeLabel));
        // Bars share the strongest programme's scale.
        Assert.Equal(100.0, rows[0].BarPercent, 6);
        Assert.Equal(11.25, rows[1].BarPercent, 6);
    }

    [Fact]
    public void ProgramComparison_StaysHiddenWithASingleProgramme()
    {
        var input = Input();
        input.StudyPrograms = StudyPrograms().Take(1).ToList();

        Assert.Empty(StatsSummaryBuilder.BuildExtended(input).ProgramComparison);
    }

    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ComposesTheSameThreeGroupsThePageRendersPhaseByPhase()
    {
        var input = Input();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var summary = StatsSummaryBuilder.Build(input);

        Assert.Equal(JsonSerializer.Serialize(StatsSummaryBuilder.BuildCore(input), options),
            JsonSerializer.Serialize(summary.Core, options));
        Assert.Equal(JsonSerializer.Serialize(StatsSummaryBuilder.BuildNotes(input), options),
            JsonSerializer.Serialize(summary.Notes, options));
        Assert.Equal(JsonSerializer.Serialize(StatsSummaryBuilder.BuildExtended(input), options),
            JsonSerializer.Serialize(summary.Extended, options));
    }

    /// <summary>The server may cache a summary, so the same input must always produce the exact
    /// same document.</summary>
    [Fact]
    public void Build_IsDeterministic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = JsonSerializer.Serialize(StatsSummaryBuilder.Build(Input()), options);
        var second = JsonSerializer.Serialize(StatsSummaryBuilder.Build(Input()), options);

        Assert.Equal(first, second);
    }
}
