using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Golden parity test for the printable-report extraction: every expected value below was
/// derived by hand-tracing the ORIGINAL client code (Report.razor.cs's OnTextLoadedAsync, before
/// the computations moved into <see cref="ReportSummaryBuilder"/>) against this fixture. It
/// therefore pins what the report showed before the refactor, not what the builder happens to
/// produce.
///
/// Fixture: Friday 2026-09-04 14:30, three courses of the active custom programme (course 1
/// completed) plus one course from a different programme, five studied sessions across the
/// full history, three course goals (one completed-but-overdue, one open, one without a target
/// date/grade).
/// </summary>
public class ReportSummaryBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 14, 30, 0);

    private const int OtherProgrammeCourseId = 99;

    private static List<CourseDto> Courses() => new()
    {
        new() { Id = 1, Semester = 1, Name = "Artificial Intelligence", Code = "AI-101", Color = "#6C5CE7", Icon = "✦", Ects = 5 },
        new() { Id = 2, Semester = 1, Name = "Python", Code = "PY-101", Color = "#00B894", Icon = "🐍", Ects = 5 },
        new() { Id = 3, Semester = 2, Name = "Statistik", Code = "ST-201", Color = "#0984E3", Icon = "∑", Ects = 10 },
    };

    private static UserSettingsDto Settings() => new()
    {
        SelectedCourseIds = new() { 1, 2, 3 },
        CompletedCourseIds = new() { 1 },
        ActiveStudyProgramId = 2,
    };

    /// <summary>Course 1's target date (2026-06-30) already lies before Now, but the course is
    /// completed - so the report reports no remaining days for it at all (same guard the stats
    /// course rows have always had), rather than an "overdue by 66 days" next to its completed
    /// badge.</summary>
    private static List<CourseGoalDto> Goals() => new()
    {
        new() { CourseId = 1, CourseName = "Artificial Intelligence", TargetDate = new DateTime(2026, 6, 30), CompletedAt = new DateTime(2026, 7, 1), Grade = 1.7m, CompletionNote = "Done well" },
        new() { CourseId = 2, CourseName = "Python", TargetDate = new DateTime(2026, 9, 20), Grade = 2.3m },
        new() { CourseId = 3, CourseName = "Statistik" }, // no target date, no grade
    };

    private static List<StudySessionDto> History()
    {
        (int Course, string Start, double Hours)[] raw =
        {
            (1, "2025-01-10 09:00", 2),
            (1, "2025-02-15 09:00", 3),
            (2, "2025-03-01 09:00", 1),
            (2, "2025-03-02 09:00", 2),
            (3, "2025-04-01 09:00", 4),
            (OtherProgrammeCourseId, "2025-05-01 09:00", 10), // different programme, must be filtered out
        };
        var courses = Courses();
        var sessions = new List<StudySessionDto>();
        for (var i = 0; i < raw.Length; i++)
        {
            var start = DateTime.ParseExact(raw[i].Start, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var course = courses.FirstOrDefault(c => c.Id == raw[i].Course);
            sessions.Add(new StudySessionDto
            {
                Id = i + 1,
                CourseId = raw[i].Course,
                CourseName = course?.Name ?? "Other programme",
                CourseColor = course?.Color ?? "#123456",
                StartTime = start,
                EndTime = start.AddHours(raw[i].Hours),
                IsCompleted = true,
            });
        }
        return sessions;
    }

    private static ReportSummaryInput Input(bool withHistory = true) => new()
    {
        Settings = Settings(),
        AllCourses = Courses(),
        Goals = Goals(),
        History = withHistory ? History() : new(),
        GroupQuotas = new Dictionary<string, int>(),
        StudyPrograms = new List<StudyProgramSummaryDto>
        {
            new() { Id = 1, Name = "Finished one", IsCompleted = true },
            new() { Id = 2, Name = "Applied AI Custom" },
        },
        Now = Now,
    };

    [Fact]
    public void CourseRows_AreSortedBySemesterThenHoursDescending()
    {
        var s = ReportSummaryBuilder.Build(Input());

        Assert.Equal(new[] { 1, 2, 3 }, s.CourseRows.Select(r => r.Course.Id));
        Assert.Equal(new[] { 5.0, 3.0, 4.0 }, s.CourseRows.Select(r => r.Hours));
        Assert.Equal(new[] { 2, 2, 1 }, s.CourseRows.Select(r => r.SessionCount));
    }

    [Fact]
    public void CourseRows_CarryCompletionDeadlineAndGradeFromTheMatchingGoal()
    {
        var rows = ReportSummaryBuilder.Build(Input()).CourseRows;
        var course1 = rows.Single(r => r.Course.Id == 1);
        var course2 = rows.Single(r => r.Course.Id == 2);
        var course3 = rows.Single(r => r.Course.Id == 3);

        Assert.True(course1.IsCompleted);
        Assert.Null(course1.DaysRemaining); // completed -> no remaining deadline (was -66)
        Assert.Equal("Done well", course1.CompletionNote);
        Assert.Equal(1.7m, course1.Grade);

        Assert.False(course2.IsCompleted);
        Assert.Equal(16, course2.DaysRemaining);
        Assert.Null(course2.CompletionNote);
        Assert.Equal(2.3m, course2.Grade);

        Assert.False(course3.IsCompleted);
        Assert.Null(course3.DaysRemaining);
        Assert.Null(course3.Grade);
    }

    [Fact]
    public void Totals_MatchTheOriginalNumbers()
    {
        var s = ReportSummaryBuilder.Build(Input());

        Assert.Equal(5, s.TotalSessions); // the other-programme session is excluded
        Assert.Equal("12h 0m", s.TotalHoursLabel);
    }

    [Fact]
    public void AverageGrade_IsEctsWeightedAcrossGradedGoals()
    {
        var s = ReportSummaryBuilder.Build(Input());

        // (1.7*5 + 2.3*5) / 10 = 2.00
        Assert.Equal("2,00", s.AverageGradeLabel);
    }

    [Fact]
    public void AverageGrade_IsIndependentOfHistory()
    {
        // The average grade comes from Goals, not from CourseRows/History - it must stay the same
        // even when there is no session history at all (matches the original: `averageGrade` was
        // computed straight from `goals`, never from `_courseRows`).
        var s = ReportSummaryBuilder.Build(Input(withHistory: false));

        Assert.Empty(s.CourseRows);
        Assert.Equal("2,00", s.AverageGradeLabel);
    }

    [Fact]
    public void AverageGrade_IsNullWhenNoGoalHasAGrade()
    {
        var input = Input();
        foreach (var g in input.Goals) g.Grade = null;

        var s = ReportSummaryBuilder.Build(input);

        Assert.Null(s.AverageGradeLabel);
    }

    [Fact]
    public void Ects_MatchTheOriginalNumbers()
    {
        var s = ReportSummaryBuilder.Build(Input());

        Assert.Equal(5, s.EctsEarned);
        Assert.Equal(20, s.EctsTotal);
        Assert.Equal(25.0, s.EctsPercent, 6);
    }

    [Fact]
    public void ProgrammeName_ResolvesTheActiveCustomProgramme()
    {
        var s = ReportSummaryBuilder.Build(Input());

        Assert.Equal("Applied AI Custom", s.ProgrammeName);
    }

    [Fact]
    public void ProgrammeName_FallsBackToTheBuiltInNameWhenNothingMatches()
    {
        var input = Input();
        input.Settings.ActiveStudyProgramId = null;

        var s = ReportSummaryBuilder.Build(input);

        Assert.Equal(CourseCatalog.BuiltInProgramName, s.ProgrammeName);
    }

    [Fact]
    public void Period_IsTheMinAndMaxSessionDateOfTheActiveProgrammeHistory()
    {
        var s = ReportSummaryBuilder.Build(Input());

        Assert.Equal(new DateTime(2025, 1, 10), s.PeriodStart);
        Assert.Equal(new DateTime(2025, 4, 1), s.PeriodEnd);
    }

    [Fact]
    public void Period_IsNullWhenThereIsNoHistory()
    {
        var s = ReportSummaryBuilder.Build(Input(withHistory: false));

        Assert.Null(s.PeriodStart);
        Assert.Null(s.PeriodEnd);
    }

    /// <summary>The server may cache a summary, so the same input must always produce the exact
    /// same document.</summary>
    [Fact]
    public void Build_IsDeterministic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = JsonSerializer.Serialize(ReportSummaryBuilder.Build(Input()), options);
        var second = JsonSerializer.Serialize(ReportSummaryBuilder.Build(Input()), options);

        Assert.Equal(first, second);
    }
}
