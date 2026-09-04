using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Golden parity test for the wrapped-page extraction: every expected value below was derived by
/// hand-tracing the ORIGINAL client code (Wrapped.razor.cs's OnTextLoadedAsync and
/// BuildAchievementCountAsync, before the computations moved into
/// <see cref="WrappedSummaryBuilder"/>) against this fixture. It therefore pins what the wrapped
/// page showed before the refactor, not what the builder happens to produce.
///
/// Fixture: Friday 2026-09-04 14:30, three courses of the active programme (course 1 completed)
/// plus one course from a different programme, 14 sessions in the 365-day recap period
/// (including one before 7am and one from 10pm on, and a 3-day consecutive streak) plus two
/// older sessions (2022/2023) that only the all-time achievements phase sees.
/// </summary>
public class WrappedSummaryBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 14, 30, 0);

    // Course 99 belongs to another programme and must be filtered out of every computation.
    private const int OtherProgrammeCourseId = 99;

    private static List<CourseDto> Courses() => new()
    {
        new() { Id = 1, Semester = 1, Name = "Artificial Intelligence", Code = "AI-101", Color = "#6C5CE7", Icon = "✦", Ects = 5 },
        new() { Id = 2, Semester = 1, Name = "Python", Code = "PY-101", Color = "#00B894", Icon = "🐍", Ects = 5 },
        new() { Id = 3, Semester = 2, Name = "Statistik", Code = "ST-201", Color = "#0984E3", Icon = "∑", Ects = 10 },
    };

    private static UserSettingsDto Settings() => new()
    {
        CompletedCourseIds = new() { 1 },
        WeeklyGoalMinHours = 4,
    };

    /// <summary>365-day recap window. All Monday dates below (07-20, 07-27, 08-03, 08-10, 08-17,
    /// 08-24, 06-01) are verified Mondays relative to Now (Friday 2026-09-04).</summary>
    private static List<StudySessionDto> PeriodSessions()
    {
        (int Course, string Start, double Hours)[] raw =
        {
            (1, "2026-07-20 09:00", 2),   // Mon
            (1, "2026-07-27 09:00", 2),   // Mon
            (1, "2026-08-03 09:00", 2),   // Mon
            (1, "2026-08-10 09:00", 2),   // Mon
            (1, "2026-08-17 09:00", 2),   // Mon
            (2, "2026-08-24 09:00", 1),   // Mon - starts a 3-day streak
            (2, "2026-08-25 09:00", 1),   // Tue
            (2, "2026-08-26 09:00", 1),   // Wed - ends the 3-day streak
            (3, "2026-08-24 15:00", 1),   // Mon, different course, same day as above (course diversity)
            (2, "2026-06-01 06:00", 1.5), // Mon, early bird (hour < 7)
            (2, "2026-06-01 22:30", 1),   // Mon, night owl (hour >= 22)
            (3, "2026-05-15 14:00", 2),   // Fri - starts a 2-day streak
            (3, "2026-05-16 14:00", 2),   // Sat - ends the 2-day streak
            (OtherProgrammeCourseId, "2026-08-05 10:00", 5), // different programme, must be filtered out
        };
        return ToSessions(raw, startId: 1);
    }

    /// <summary>Only visible to the all-time achievements phase, never to the 365-day recap.</summary>
    private static List<StudySessionDto> OldSessions()
    {
        (int Course, string Start, double Hours)[] raw =
        {
            (2, "2023-01-10 10:00", 3), // Tue, outside the 365-day recap window
            (1, "2022-05-05 08:00", 2), // Thu, outside the 365-day recap window
            (OtherProgrammeCourseId, "2021-01-01 09:00", 4), // different programme, must be filtered out
        };
        return ToSessions(raw, startId: 100);
    }

    private static List<StudySessionDto> ToSessions((int Course, string Start, double Hours)[] raw, int startId)
    {
        var courses = Courses();
        var sessions = new List<StudySessionDto>();
        for (var i = 0; i < raw.Length; i++)
        {
            var start = DateTime.ParseExact(raw[i].Start, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var course = courses.FirstOrDefault(c => c.Id == raw[i].Course);
            sessions.Add(new StudySessionDto
            {
                Id = startId + i,
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

    private static List<NoteDto> Notes() => new()
    {
        new() { Id = 1, Title = "General", Content = "note", CourseId = null },
        new() { Id = 2, Title = "Python note", Content = "note", CourseId = 2 },
        new() { Id = 3, Title = "Other programme", Content = "hidden", CourseId = OtherProgrammeCourseId },
    };

    private static WrappedSummaryInput Input(bool includeAllTime = true) => new()
    {
        Settings = Settings(),
        AllCourses = Courses(),
        PeriodHistory = PeriodSessions(),
        AllTimeHistory = includeAllTime ? PeriodSessions().Concat(OldSessions()).ToList() : new(),
        GroupQuotas = new Dictionary<string, int>(),
        StudyPrograms = new List<StudyProgramSummaryDto>
        {
            new() { Id = null, Name = "Built-in", IsBuiltIn = true },
            new() { Id = 1, Name = "Finished one", IsCompleted = true },
            new() { Id = 2, Name = "Also finished", IsCompleted = true },
            new() { Id = 3, Name = "Running one" },
        },
        Notes = Notes(),
        Now = Now,
    };

    [Fact]
    public void Recap_TotalsMatchTheOriginalNumbers()
    {
        var r = WrappedSummaryBuilder.BuildRecap(Input());

        Assert.Equal(20.5, r.TotalHours, 6);
        Assert.Equal("20h 30m", r.TotalHoursLabel);
        Assert.Equal(13, r.TotalSessions); // the other-programme session is excluded
        Assert.Equal(3, r.LongestStreak);
    }

    [Fact]
    public void Recap_TopCourseIsTheHighestHoursCourseOfTheActiveProgramme()
    {
        var r = WrappedSummaryBuilder.BuildRecap(Input());

        Assert.NotNull(r.TopCourse);
        Assert.Equal(1, r.TopCourse!.CourseId);
        Assert.Equal("Artificial Intelligence", r.TopCourse.Name);
        Assert.Equal("✦", r.TopCourse.Icon);
        Assert.Equal("#6C5CE7", r.TopCourse.Color);
        Assert.Equal(10.0, r.TopCourse.Hours, 6);
    }

    [Fact]
    public void Recap_BusiestWeekdayIsMonday()
    {
        var r = WrappedSummaryBuilder.BuildRecap(Input());

        Assert.NotNull(r.BusiestWeekday);
        Assert.Equal(0, r.BusiestWeekday!.Index); // 0 = Monday
        Assert.Equal(14.5, r.BusiestWeekday.Hours, 6);
    }

    [Fact]
    public void Recap_ChronotypeHours_MatchTheOriginalNumbers()
    {
        var r = WrappedSummaryBuilder.BuildRecap(Input());

        Assert.Equal(1.5, r.EarlyBirdHours, 6);
        Assert.Equal(1.0, r.NightOwlHours, 6);
    }

    [Fact]
    public void Recap_EmptyPeriodHistory_ProducesTheZeroValueRecap()
    {
        var input = Input();
        input.PeriodHistory = new();

        var r = WrappedSummaryBuilder.BuildRecap(input);

        Assert.Equal(0.0, r.TotalHours);
        Assert.Equal("0h 0m", r.TotalHoursLabel);
        Assert.Equal(0, r.TotalSessions);
        Assert.Equal(0, r.LongestStreak);
        Assert.Null(r.TopCourse);
        Assert.Null(r.BusiestWeekday);
        Assert.Equal(0.0, r.EarlyBirdHours);
        Assert.Equal(0.0, r.NightOwlHours);
    }

    [Fact]
    public void Achievements_KeepTheFortyFourTiersAndTheirOrder()
    {
        var a = WrappedSummaryBuilder.BuildAchievements(Input());

        Assert.Equal(44, a.Total);
        // Unlocked: 25h-tier (25.5h total), 1-course-completed, 2h-marathon (the 3h old session),
        // 1-perfect-week (2 weeks reach the 4h/week goal), 2-course-diversity (one week has both
        // course 2 and 3), and 2 of the 3 programme tiers (2 completed programmes).
        Assert.Equal(7, a.Unlocked);
    }

    [Fact]
    public void Achievements_NoAllTimeHistory_OnlyCourseAndProgrammeTiersSurvive()
    {
        var a = WrappedSummaryBuilder.BuildAchievements(Input(includeAllTime: false));

        // coursesCompleted/ectsTotal/ectsEarned/notesCount/programsCompleted all come from
        // settings/courses/notes/programmes, not from session history, so they survive even with
        // a completely empty all-time history: "1 course completed" (threshold 1) and 2 of the 3
        // programme tiers (thresholds 1 and 2, programsCompleted=2).
        Assert.Equal(3, a.Unlocked);
    }

    /// <summary>The server may cache a summary, so the same input must always produce the exact
    /// same document.</summary>
    [Fact]
    public void Build_IsDeterministic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = JsonSerializer.Serialize(WrappedSummaryBuilder.Build(Input()), options);
        var second = JsonSerializer.Serialize(WrappedSummaryBuilder.Build(Input()), options);

        Assert.Equal(first, second);
    }
}
