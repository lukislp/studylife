using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Golden parity test for the dashboard extraction: every expected value below was derived by
/// hand-tracing the ORIGINAL client code (Index.razor.cs's LoadDataAsync plus the
/// Index.Achievements/Forecast/Insights partials, before the computations moved into
/// <see cref="DashboardSummaryBuilder"/>) against this fixture. It therefore pins what the
/// dashboard showed before the refactor, not what the builder happens to produce.
///
/// Fixture: Friday 2026-09-04 14:30, three courses of the active programme (one completed) plus
/// one course from a different programme, 30 sessions spread over 13 months - completed and
/// open, past, running, later today and in the future - three course goals and three study
/// programmes.
/// </summary>
public class DashboardSummaryBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 14, 30, 0);

    // Courses 1-3 are the active programme; 99 belongs to another one and must be filtered out
    // of everything except the (deliberately programme-agnostic) inactivity nudge.
    private const int OtherProgrammeCourseId = 99;

    private static List<CourseDto> Courses() => new()
    {
        new() { Id = 1, Semester = 1, Name = "Artificial Intelligence", Code = "AI-101", Color = "#6C5CE7", Icon = "✦", Ects = 5, Topics = new() { "T1", "T2", "T3" } },
        new() { Id = 2, Semester = 1, Name = "Python", Code = "PY-101", Color = "#00B894", Icon = "🐍", Ects = 5, Topics = new() { "A", "B" } },
        new() { Id = 3, Semester = 2, Name = "Statistik", Code = "ST-201", Color = "#0984E3", Icon = "∑", Ects = 10 },
    };

    private static UserSettingsDto Settings() => new()
    {
        SelectedCourseIds = new() { 1, 2, 3 },
        CompletedCourseIds = new() { 1 },
        WeeklyGoalMinHours = 25,
        WeeklyGoalMaxHours = 30,
        MonthlyGoalMinHours = 100,
        MonthlyGoalMaxHours = 130,
        InactivityThresholdDays = 5,
        StudyDays = "0,1,2,3,4,5,6",
        StudyWindowStartHour = 8,
        StudyWindowEndHour = 21,
        TargetGraduationDate = new DateTime(2027, 9, 30),
        LastBackupDownloadAt = null,
    };

    /// <summary>Ids are the 1-based position in this list, so the assertions can name sessions.</summary>
    private static List<StudySessionDto> AllSessions()
    {
        (int Course, string Start, double Hours, bool Completed)[] raw =
        {
            (1, "2025-08-20 10:00", 2, true),
            (1, "2025-09-10 10:00", 3, true),
            (1, "2026-05-12 09:00", 2, true),
            (1, "2026-06-15 09:00", 4, true),
            (2, "2026-07-02 08:00", 5, true),
            (2, "2026-08-03 09:00", 2, true),
            (3, "2026-08-04 14:00", 2, true),
            (2, "2026-08-05 19:00", 3, true),
            (1, "2026-08-06 09:00", 2, true),
            (2, "2026-08-10 09:00", 3, true),
            (3, "2026-08-11 09:00", 2, true),
            (2, "2026-08-12 09:00", 2, true),
            (3, "2026-08-13 20:00", 2, true),
            (2, "2026-08-17 09:00", 3, true),
            (3, "2026-08-18 09:00", 3, true),
            (2, "2026-08-19 09:00", 2, true),
            (1, "2026-08-20 09:00", 2, true),
            (2, "2026-08-24 09:00", 3, true),
            (3, "2026-08-25 09:00", 2, true),
            (2, "2026-08-26 09:00", 2, true),
            (1, "2026-08-27 09:00", 2, true),
            (OtherProgrammeCourseId, "2026-08-28 09:00", 2, true),
            (2, "2026-08-31 09:00", 2, true),
            (3, "2026-09-01 09:00", 1, true),
            (2, "2026-09-02 09:00", 1, true),
            (1, "2026-09-03 09:00", 1, true),
            (2, "2026-09-04 09:00", 1.5, true),
            (3, "2026-09-04 14:00", 1, false),   // running right now
            (1, "2026-09-04 18:00", 2, false),   // later today
            (2, "2026-09-06 10:00", 2, false),   // future
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
                IsCompleted = raw[i].Completed,
            });
        }
        return sessions;
    }

    private static List<CourseGoalDto> Goals() => new()
    {
        new() { CourseId = 1, CourseName = "Artificial Intelligence", TargetDate = new DateTime(2026, 6, 30), CompletedAt = new DateTime(2026, 7, 1), Grade = 1.7m, CompletedTopics = "T1,T2,T3", Tag = "done" },
        new() { CourseId = 2, CourseName = "Python", TargetDate = new DateTime(2026, 9, 20), Grade = 2.3m, CompletedTopics = "A", Tag = "exam" },
        new() { CourseId = 3, CourseName = "Statistik", TargetDate = new DateTime(2026, 12, 15), CompletedTopics = "" },
    };

    /// <summary>200 characters, so the 120-character excerpt cut plus TrimEnd is exercised.</summary>
    private static readonly string LongNoteContent = string.Concat(Enumerable.Repeat("word ", 40));

    private static List<NoteDto> Notes() => new()
    {
        new() { Id = 1, Title = "Latest", Content = LongNoteContent, CourseId = 2, UpdatedAt = new DateTime(2026, 9, 4, 10, 0, 0) },
        new() { Id = 2, Title = "General", Content = "short", CourseId = null, UpdatedAt = new DateTime(2026, 9, 3, 10, 0, 0) },
        new() { Id = 3, Title = "Other programme", Content = "hidden", CourseId = OtherProgrammeCourseId, UpdatedAt = new DateTime(2026, 9, 2, 10, 0, 0) },
    };

    private static DashboardSummaryInput Input(bool withHeavyHistory = true)
    {
        var all = AllSessions();
        return new DashboardSummaryInput
        {
            Settings = Settings(),
            AllCourses = Courses(),
            // The three fetch windows the page uses, applied to the same master list.
            Sessions = all.Where(s => s.StartTime >= Now.AddDays(-7) && s.StartTime <= Now.AddDays(90)).ToList(),
            History = all.Where(s => s.StartTime >= Now.AddDays(-DashboardSummaryBuilder.HistoryDays)).ToList(),
            HeavyHistory = withHeavyHistory ? all.Where(s => StudyMetrics.IsStudied(s, Now)).ToList() : null,
            Goals = Goals(),
            GroupQuotas = new Dictionary<string, int>(),
            StudyPrograms = new List<StudyProgramSummaryDto>
            {
                new() { Id = null, Name = "Built-in", IsBuiltIn = true },
                new() { Id = 1, Name = "Finished one", IsCompleted = true },
                new() { Id = 2, Name = "Running one" },
            },
            Notes = Notes(),
            IsOwner = true,
            IsDemo = false,
            RawBackupSupported = true,
            Now = Now,
        };
    }

    [Fact]
    public void CourseList_ContainsOnlySelectedCoursesOfTheActiveProgramme()
    {
        var summary = DashboardSummaryBuilder.Build(Input());

        Assert.Equal(new[] { 1, 2, 3 }, summary.Courses.Select(c => c.Id));
    }

    [Fact]
    public void TodayActiveAndUpcomingSessions_AreScopedAndOrdered()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        Assert.Equal(new[] { 27, 28, 29 }, s.TodaySessions.Select(x => x.Id));
        Assert.Equal(28, s.ActiveSession?.Id);
        Assert.Equal(29, s.UpcomingSession?.Id);
        Assert.Equal(new[] { 27, 26, 25, 24, 23 }, s.RecentSessions.Select(x => x.Id));
    }

    [Fact]
    public void WeekStatsAndStreak_MatchTheOriginalNumbers()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        // studied-only: of the week's 8 sessions, #28 (running until 15:00), #29 (18:00 today) and
        // #30 (Sunday) have not been studied yet -> 5 sessions, 2+1+1+1+1.5 = 6h 30m
        // (was 8 / "11h 30m" when the three planned ones still counted).
        Assert.Equal(5, s.WeekSessions);
        Assert.Equal("6h 30m", s.WeekHoursLabel);
        Assert.Equal(5, s.Streak);
        Assert.Equal(5, s.LongestStreak);
        // Like-for-like delta: 5 weekdays elapsed (Mon 31.08. - Fri 04.09.), so the previous week
        // counts only Mon 24.08. - Fri 28.08. = 3+2+2+2 = 9h against this week's 6.5h -> 2h 30m
        // DOWN (it read "up" before, comparing 11.5 planned hours against a full previous week).
        Assert.Equal("2h 30m", s.WeekDeltaLabel);
        Assert.False(s.WeekDeltaUp);
    }

    [Fact]
    public void FocusScore_CountsOnlySessionsAlreadyStudied()
    {
        var focus = DashboardSummaryBuilder.Build(Input()).Sessions.FocusScore;

        Assert.True(focus.Visible);
        Assert.Equal(3, focus.Planned);
        Assert.Equal(1, focus.Studied);
        Assert.Equal(100.0 / 3, focus.Percent, 6);
    }

    /// <summary>The two quota tiles are the deliberate exception to "hours = studied": they answer
    /// "how much is on the plan for this week/month?", so every session of the window counts,
    /// planned ones included - these numbers are therefore unchanged.</summary>
    [Fact]
    public void Quotas_CountPlannedSessionsToo_AndMatchTheOriginalPercentsAndLabels()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        Assert.Equal("11h 30m", s.WeekQuota.HoursLabel);
        Assert.Equal(25, s.WeekQuota.TargetMin);
        Assert.Equal(30, s.WeekQuota.TargetMax);
        Assert.Equal(33.333333, s.WeekQuota.Percent, 5);
        Assert.Equal(72.463768, s.WeekQuota.MinPercent, 5);
        Assert.True(s.WeekQuota.Warning);
        Assert.Equal("13h 30m", s.WeekQuota.MissingLabel);

        Assert.Equal("9h 30m", s.MonthQuota.HoursLabel);
        Assert.Equal(100, s.MonthQuota.TargetMin);
        Assert.Equal(130, s.MonthQuota.TargetMax);
        Assert.Equal(6.354515, s.MonthQuota.Percent, 5);
        Assert.Equal(66.889632, s.MonthQuota.MinPercent, 5);
        Assert.True(s.MonthQuota.Warning);
        Assert.Equal("90h 30m", s.MonthQuota.MissingLabel);
    }

    [Fact]
    public void WeeklyTrend_HasEightWeeksOldestFirstAndScalesToThePeak()
    {
        var trend = DashboardSummaryBuilder.Build(Input()).Sessions.WeeklyTrend;

        Assert.Equal(new[] { "13.07", "20.07", "27.07", "03.08", "10.08", "17.08", "24.08", "31.08" },
            trend.Select(t => t.Label));
        // studied-only: only the current bar moves - the three planned sessions (1+2+2 h) drop out
        // of the 31.08. week, 11.5 -> 6.5. Every earlier week is fully in the past and unchanged.
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 9.0, 9.0, 10.0, 9.0, 6.5 }, trend.Select(t => t.Hours));
        // The peak is now the 17.08. week (10h), not the current one: 10/10 vs. 6.5/10.
        Assert.Equal(100.0, trend[5].Percent, 6);
        Assert.Equal(65.0, trend[7].Percent, 6);
        Assert.True(trend[7].IsCurrent);
        Assert.All(trend.Take(7), t => Assert.False(t.IsCurrent));
    }

    [Fact]
    public void TodayRingAndStreakStrip_MatchTheOriginalNumbers()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        // studied-only: of today's three sessions only #27 (1.5h, completed) counts - #28 runs
        // until 15:00 and #29 starts at 18:00. 1.5h against the 27.5/2/7 = 3.9286h daily target =
        // 38.18% (it used to read 4h 30m / a full, "exceeded" ring off two not-yet-studied hours).
        Assert.Equal(38.181818, s.TodayRing.RingPercent, 5);
        Assert.False(s.TodayRing.Exceeded);
        Assert.Equal("1h 30m", s.TodayRing.HoursLabel);
        Assert.Equal("3.9h", s.TodayRing.DailyTargetLabel);

        Assert.Equal(new[] { "Sat", "Sun", "Mon", "Tue", "Wed", "Thu", "Fri" }, s.StreakStrip.Select(d => d.Label));
        Assert.Equal(new[] { false, false, true, true, true, true, true }, s.StreakStrip.Select(d => d.Studied));
        Assert.Equal(new[] { false, false, false, false, false, false, true }, s.StreakStrip.Select(d => d.IsToday));
    }

    [Fact]
    public void MiniDonut_RanksCoursesByHoursOverThirtyDays()
    {
        var donut = DashboardSummaryBuilder.Build(Input()).Sessions.MiniDonut;

        Assert.Equal(39.5, donut.TotalHours, 6);
        Assert.Equal(new[] { 2, 3, 1 }, donut.Slices.Select(x => x.CourseId));
        Assert.Equal(new[] { 22.5, 10.0, 7.0 }, donut.Slices.Select(x => x.Hours));
        Assert.Equal(56.9620253, donut.Slices[0].Percent, 6);
        Assert.Equal("#00B894", donut.Slices[0].Color);
        Assert.All(donut.Slices, x => Assert.False(x.IsOther));
        Assert.StartsWith("conic-gradient(#00B894 0% ", donut.Gradient);
        Assert.EndsWith("100%)", donut.Gradient);
    }

    [Fact]
    public void NeglectedCourse_IsTheActiveCourseStudiedLongestAgo()
    {
        var pick = DashboardSummaryBuilder.Build(Input()).Sessions.NeglectedCourse;

        Assert.NotNull(pick);
        Assert.Equal(3, pick!.CourseId);
        Assert.Equal("Statistik", pick.Name);
        Assert.Equal(3, pick.DaysSinceLastStudied);
    }

    [Fact]
    public void Insights_PickTheMorningBucketAndConfirmThePlannedSession()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        Assert.True(s.ProductivityHint.Visible);
        Assert.Equal(2, s.ProductivityHint.BestBucketIndex); // 9-12
        Assert.True(s.ProductivityHint.Planned);
        Assert.Equal("09:00", s.ProductivityHint.PlannedStartTimeLabel);
        Assert.False(s.ProductivityHint.ShowSuggestText);
        Assert.False(s.ProductivityHint.ShowPlanLink);

        // Monday leads with 17 of 59.5 hours - below the 30% share the insight requires.
        Assert.False(s.WeekdayInsight.Available);
    }

    [Fact]
    public void BannersStayQuiet_ExceptTheNeverDownloadedBackupHint()
    {
        var s = DashboardSummaryBuilder.Build(Input()).Sessions;

        Assert.False(s.Inactivity.Show);
        Assert.Equal(0, s.Inactivity.DaysSinceLastSession);

        // This week (6.5h so far) is well above the 4.625h baseline average.
        Assert.False(s.AnomalyHint.Show);

        Assert.True(s.BackupHint.Show);
        Assert.True(s.BackupHint.NeverDownloaded);
        Assert.Equal(0, s.BackupHint.DaysSinceLastBackup);
    }

    [Fact]
    public void LatestNote_IsScopedToTheProgrammeAndExcerpted()
    {
        var note = DashboardSummaryBuilder.Build(Input()).Sessions.LatestNote;

        Assert.Equal(2, note.NotesCount); // the other programme's note is filtered out
        Assert.Equal(1, note.Note?.Id);
        Assert.Equal("Python", note.CourseName);
        Assert.Equal(string.Concat(Enumerable.Repeat("word ", 23)) + "word…", note.Excerpt);
    }

    [Fact]
    public void Goals_CarryTagsDeadlinesEctsGradeAndTopics()
    {
        var g = DashboardSummaryBuilder.Build(Input()).Goals;

        Assert.Equal(3, g.CourseTags.Count);
        Assert.Equal("done", g.CourseTags[1]);
        Assert.Equal("exam", g.CourseTags[2]);
        Assert.Null(g.CourseTags[3]);
        // Only course 2: course 1's goal is completed, course 3's target date is 102 days out.
        Assert.Equal(new[] { 2 }, g.CourseDeadlineDays.Keys);
        Assert.Equal(16, g.CourseDeadlineDays[2]);

        Assert.Equal(new[] { "Python", "Statistik" }, g.UpcomingGoals.Select(x => x.CourseName));
        Assert.Equal(new[] { 16, 102 }, g.UpcomingGoals.Select(x => x.DaysLeft));
        Assert.Equal(new DateTime(2026, 9, 20), g.UpcomingGoals[0].TargetDate!.Value);

        Assert.Equal(5, g.EctsEarned);
        Assert.Equal(20, g.EctsTotal);
        Assert.Equal(25.0, g.EctsPercent, 6);
        Assert.Equal("2,00", g.AverageGradeLabel);

        Assert.Equal(4, g.TopicsCompleted);
        Assert.Equal(5, g.TopicsTotal);
        Assert.Equal(80.0, g.TopicsPercent, 6);

        Assert.Equal(1, g.ProgramsCompleted);
    }

    [Fact]
    public void ForecastAndGraduationGoal_MatchTheOriginalNumbers()
    {
        var p = DashboardSummaryBuilder.Build(Input()).Progress;

        // 15 ECTS left over 2 semesters = 39 baseline weeks; the 5.44 h/week recent pace clamps
        // the pace ratio at 0.25, so 156 weeks = 1092 days from today.
        Assert.True(p.Forecast.Available);
        Assert.False(p.Forecast.AlreadyDone);
        Assert.Equal("31.08.2029", p.Forecast.DateLabel);

        Assert.True(p.GraduationGoal.Visible);
        Assert.False(p.GraduationGoal.Expired);
        Assert.False(p.GraduationGoal.OnTrack);
        Assert.Equal("19,2", p.GraduationGoal.RequiredValue);
        Assert.Equal("5,4", p.GraduationGoal.PaceValue);
        Assert.Equal("30.09.2027", p.GraduationGoal.TargetDateValue);
    }

    [Fact]
    public void MonthComparisonAndBestRecords_MatchTheOriginalNumbers()
    {
        var p = DashboardSummaryBuilder.Build(Input()).Progress;

        Assert.Equal("4h 30m", p.MonthComparison.CurrentLabel);
        Assert.Equal("34h 30m", p.MonthComparison.VsLastMonthLabel);
        Assert.False(p.MonthComparison.VsLastMonthUp);
        Assert.True(p.MonthComparison.HasYearData);
        Assert.Equal("1h 30m", p.MonthComparison.VsLastYearLabel);
        Assert.True(p.MonthComparison.VsLastYearUp);

        Assert.Equal("5h", p.BestRecords.BestDayHoursLabel);
        Assert.Equal("02.07.2026", p.BestRecords.BestDayDateLabel);
        Assert.False(p.BestRecords.BestDayIsNew);
        Assert.Equal("10h", p.BestRecords.BestWeekHoursLabel);
        Assert.Equal("17.08. – 23.08.2026", p.BestRecords.BestWeekRangeLabel);
        Assert.False(p.BestRecords.BestWeekIsNew);
    }

    [Fact]
    public void Achievements_KeepTheFortyFourTiersAndTheirOrder()
    {
        var a = DashboardSummaryBuilder.Build(Input()).Progress.Achievements;

        Assert.Equal(44, a.Total);
        Assert.Equal(44, a.Tiers.Count);
        Assert.Equal(6, a.Unlocked);

        Assert.Equal(AchievementCatalog.HoursKey, a.Tiers[0].Category);
        Assert.Equal(25, a.Tiers[0].Threshold);
        Assert.True(a.Tiers[0].Unlocked);
        Assert.Equal(59.5, a.Tiers[0].Current, 6);

        Assert.Equal(AchievementCatalog.AllCoursesKey, a.Tiers[17].Category);
        Assert.False(a.Tiers[17].Unlocked);
        Assert.Equal(AchievementCatalog.ProgramsKey, a.Tiers[^1].Category);
        Assert.Equal(3, a.Tiers[^1].Threshold);
    }

    [Fact]
    public void WeekdayInsight_BecomesAvailableWhenOneWeekdayDominates()
    {
        var input = Input();
        // Ten studied Monday sessions and nothing else: one weekday holds 100% of the hours.
        var mondays = Enumerable.Range(0, 10)
            .Select(i => new StudySessionDto
            {
                Id = 1000 + i,
                CourseId = 2,
                StartTime = new DateTime(2026, 6, 1, 9, 0, 0).AddDays(-7 * i),
                EndTime = new DateTime(2026, 6, 1, 11, 0, 0).AddDays(-7 * i),
                IsCompleted = true,
            })
            .ToList();
        input.History = mondays;
        input.Sessions = new List<StudySessionDto>();

        var insight = DashboardSummaryBuilder.BuildSessions(input).WeekdayInsight;

        Assert.True(insight.Available);
        Assert.Equal(0, insight.BestIndex); // 0 = Monday
    }

    [Fact]
    public void HeavyHistoryNull_BehavesLikeAnEmptyAllTimeHistory()
    {
        // Mirrors the client's throttle: when the ~10-year fetch is skipped and no cache exists,
        // the phase-5 tiles fall back to their "no data" values instead of failing.
        var p = DashboardSummaryBuilder.Build(Input(withHeavyHistory: false)).Progress;

        Assert.Equal("0h", p.BestRecords.BestDayHoursLabel);
        Assert.Equal("–", p.BestRecords.BestDayDateLabel);
        Assert.Equal("0h", p.BestRecords.BestWeekHoursLabel);
        Assert.Equal("–", p.BestRecords.BestWeekRangeLabel);
        Assert.False(p.MonthComparison.HasYearData);
        // Only the "one course completed" and "one programme completed" tiers survive.
        Assert.Equal(2, p.Achievements.Unlocked);
    }

    /// <summary>The server may cache a summary, so the same input must always produce the exact
    /// same document.</summary>
    [Fact]
    public void Build_IsDeterministic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = JsonSerializer.Serialize(DashboardSummaryBuilder.Build(Input()), options);
        var second = JsonSerializer.Serialize(DashboardSummaryBuilder.Build(Input()), options);

        Assert.Equal(first, second);
    }
}
