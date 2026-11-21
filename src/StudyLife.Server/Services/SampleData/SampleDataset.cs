// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.SampleData;

/// <summary>
/// The sample study dataset, written into ONE existing account: a student in the built-in
/// "Applied Artificial Intelligence" program, semester 1 fully completed (grades + ECTS
/// progress), currently mid-semester-2 with an active study streak ending today, a day in
/// progress (a session done this morning, one planned for later today), a handful of planned
/// sessions in the coming days, two upcoming exam goals and a few notes. In German
/// (the default, and the demo's unless Demo:Language says otherwise) courses and topics come straight from
/// CourseCatalog.AppliedAICourses; in English the account gets its own copy of that program
/// with English names and topics (<see cref="EnglishSampleProgram"/>). The free texts per
/// language are <see cref="SampleDataContent"/>; the structure is the same for both.
///
/// Every date is generated relative to the moment of the call, so the data always looks current.
/// The content itself is deterministic (seeded Random): two calls on the same day produce the
/// identical dataset.
///
/// This class only ADDS rows for the given account. Who the account is, whether seeding is
/// allowed at all and what has to be wiped first is decided by the callers
/// (<see cref="DemoSeeder"/> for demo instances, <see cref="SampleAccountSeeder"/> for an
/// operator-chosen account on a normal installation).
/// </summary>
public static class SampleDataset
{
    private const int RandomSeed = 20260805;

    /// <summary>The most recent days that all have a completed session (the live streak).</summary>
    private const int LiveStreakDays = 12;

    /// <summary>The earlier month keeps at most this share of the hours the current month has so far.</summary>
    private const double EarlierMonthShare = 0.8;

    /// <summary>
    /// Keeps the demo's month comparison ("vs last month") from showing an artificial drop. The app
    /// compares the month in progress with the WHOLE earlier month, so an evenly spread history
    /// always reads as a fall, whatever the day of the month. The earlier month is therefore thinned
    /// out (every other session first, never the live-streak days) until it holds at most
    /// <see cref="EarlierMonthShare"/> of what the current month has so far; the dashboard then
    /// shows growth. Does nothing while the current month has no history yet.
    /// </summary>
    internal static void ShapeMonthTrend(List<StudySessionEntity> history, DateTime today)
    {
        static double Hours(StudySessionEntity s) => (s.EndTime - s.StartTime).TotalHours;

        var thisMonth = history.Where(s => s.StartTime.Year == today.Year && s.StartTime.Month == today.Month).Sum(Hours);
        if (thisMonth <= 0) return;

        var earlier = today.AddMonths(-1);
        var inEarlierMonth = history.Where(s => s.StartTime.Year == earlier.Year && s.StartTime.Month == earlier.Month).ToList();
        var removable = inEarlierMonth.Where(s => s.StartTime.Date < today.AddDays(-LiveStreakDays)).OrderBy(s => s.StartTime).ToList();
        var candidates = removable.Where((_, i) => i % 2 == 1).Concat(removable.Where((_, i) => i % 2 == 0));

        var total = inEarlierMonth.Sum(Hours);
        foreach (var session in candidates)
        {
            if (total <= thisMonth * EarlierMonthShare) break;
            history.Remove(session);
            total -= Hours(session);
        }
    }

    /// <summary>Today's finished session: early morning, before a typical day starts.</summary>
    internal static readonly TimeSpan TodaysCompletedStart = new(7, 30, 0);
    internal static readonly TimeSpan TodaysCompletedLength = TimeSpan.FromMinutes(60);

    /// <summary>Today's planned session starts at 14:00, or two full hours after "now" when that is
    /// later; none once it would start after 20:00 (it lasts 90 minutes).</summary>
    internal static DateTime? PlannedStartToday(DateTime now)
    {
        var hour = Math.Max(14, now.Hour + 2);
        return hour <= 20 ? now.Date.AddHours(hour) : null;
    }

    /// <summary>Length of a session the goal plan adds (same as every other planned session).</summary>
    internal static readonly TimeSpan PlannedLength = TimeSpan.FromMinutes(90);

    /// <summary>Start of a session the goal plan adds: an evening slot.</summary>
    internal static readonly TimeSpan GoalPlanStart = new(18, 0, 0);

    /// <summary>
    /// Plans the rest of the current week and month up to the minimum study goals, the way a
    /// student fills the calendar with the planner. The dashboard's quota tiles count planned
    /// sessions of the week/month too, so without this the seeded demo read "below target" on
    /// most days (a Monday morning has hardly anything studied yet). Free future days are filled
    /// in order, weekdays before weekends, one evening session each, until both minimums are met
    /// or no free day within the week/month is left. Returns only the added sessions;
    /// <paramref name="plan"/> builds one from its start and a running number.
    /// </summary>
    internal static List<StudySessionEntity> PlanUpToGoals(IReadOnlyList<StudySessionEntity> sessions, DateTime now,
        SampleStudyGoals goals, Func<DateTime, int, StudySessionEntity> plan)
    {
        var today = now.Date;
        var weekStart = StudyMetrics.WeekStartOf(today);
        var weekEnd = weekStart.AddDays(7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var all = sessions.ToList();
        var added = new List<StudySessionEntity>();

        double HoursBetween(DateTime from, DateTime to) => all
            .Where(s => s.StartTime.Date >= from && s.StartTime.Date < to)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var lastDay = weekEnd > monthEnd ? weekEnd : monthEnd;
        var freeDays = Enumerable.Range(1, (lastDay - today).Days - 1)
            .Select(offset => today.AddDays(offset))
            .Where(day => !all.Any(s => s.StartTime.Date == day))
            .OrderBy(day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            .ThenBy(day => day)
            .ToList();

        foreach (var day in freeDays)
        {
            var weekShort = HoursBetween(weekStart, weekEnd) < goals.WeeklyMinHours;
            var monthShort = HoursBetween(monthStart, monthEnd) < goals.MonthlyMinHours;
            if (!weekShort && !monthShort) break;
            if (!(weekShort && day < weekEnd) && !(monthShort && day < monthEnd)) continue;

            var session = plan(day.Add(GoalPlanStart), added.Count);
            added.Add(session);
            all.Add(session);
        }
        return added;
    }

    /// <summary>Days before today the first semester's history starts and ends - it stays clear of
    /// the current semester's 70-day history and of the month comparison's earlier month.</summary>
    internal const int EarlierSemesterFirstDay = 235;
    internal const int EarlierSemesterLastDay = 76;

    /// <summary>
    /// Study history of the completed first semester, so the stats page's semester comparison
    /// sets the current semester against a previous one with real hours (without it the previous
    /// semester read "0h 0m"). Sessions run from about eight months ago until shortly before the
    /// current semester's history starts, each on a semester-1 course that was not yet completed
    /// on that day. Its own seeded Random, so the current semester's sessions stay exactly as they
    /// were.
    /// </summary>
    internal static List<StudySessionEntity> EarlierSemesterHistory(int userId, IReadOnlyList<CourseDto> semester1,
        IReadOnlyList<DateTime> completedAt, DateTime today)
    {
        var rng = new Random(RandomSeed + 1);
        var durations = new[] { 45, 60, 60, 90, 90, 120 };
        var sessions = new List<StudySessionEntity>();
        for (var dayOffset = EarlierSemesterFirstDay; dayOffset >= EarlierSemesterLastDay; dayOffset--)
        {
            var day = today.AddDays(-dayOffset);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (rng.NextDouble() > (isWeekend ? 0.25 : 0.65)) continue;

            var open = semester1.Where((_, i) => completedAt[i].Date > day).ToList();
            if (open.Count == 0) continue;

            var course = open[rng.Next(open.Count)];
            var minutes = durations[rng.Next(durations.Length)];
            var start = day.AddHours(9 + rng.Next(0, 4)).AddMinutes(15 * rng.Next(0, 4));
            sessions.Add(new StudySessionEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = start,
                EndTime = start.AddMinutes(minutes),
                Topic = course.Topics[rng.Next(course.Topics.Count)],
                IsCompleted = true,
                TimerModeId = 1 + rng.Next(0, 3),
            });
        }
        return sessions;
    }

    /// <summary>
    /// The account still has the entity's default study goals (25-30 h a week, 100-130 h a month),
    /// i.e. it never set its own. Only then may the sample goals replace them; an account whose
    /// goals differ keeps them. (An account that deliberately saved exactly the defaults cannot be
    /// told apart; it gets the sample goals, which is what the sample data is for.)
    /// </summary>
    internal static bool HasDefaultStudyGoals(UserSettingsEntity settings)
    {
        var defaults = new UserSettingsEntity();
        return settings.WeeklyGoalMinHours == defaults.WeeklyGoalMinHours
            && settings.WeeklyGoalMaxHours == defaults.WeeklyGoalMaxHours
            && settings.MonthlyGoalMinHours == defaults.MonthlyGoalMinHours
            && settings.MonthlyGoalMaxHours == defaults.MonthlyGoalMaxHours;
    }

    /// <summary>Months the seeded graduation target lies after the forecast date.</summary>
    internal const int GraduationTargetMarginMonths = 3;

    /// <summary>
    /// A desired graduation date the seeded pace actually reaches: the end of the month
    /// <see cref="GraduationTargetMarginMonths"/> months after the forecast the dashboard computes
    /// from the same data (<see cref="StudyMetrics.CalcForecast"/>, the shared implementation, so
    /// nothing of its model is repeated here). The forecast date is exactly where the required
    /// weekly hours of the graduation goal card equal the recent pace, so any later target reads
    /// "on track" at seed time; a fixed "two years from now" read "behind pace" because the sample
    /// student studies part-time (about 11-12 h a week). Falls back to two years and three months
    /// when no forecast is possible.
    /// </summary>
    internal static DateTime GraduationTarget(IReadOnlyList<CourseDto> catalog, IReadOnlyDictionary<string, int> groupQuotas,
        IEnumerable<int> completedCourseIds, IEnumerable<StudySessionEntity> sessions, int weeklyGoalMinHours,
        int weeklyGoalMaxHours, DateTime now)
    {
        var forecast = StudyMetrics.CalcForecast(
            CourseCatalog.CalcTotalEcts(catalog, groupQuotas),
            CourseCatalog.CalcEctsEarned(catalog, completedCourseIds, groupQuotas),
            catalog, weeklyGoalMinHours, weeklyGoalMaxHours, sessions.Select(SessionService.ToDto), now);
        if (forecast.ForecastDate is not { } forecastDate) return now.Date.AddYears(2).AddMonths(3);

        return new DateTime(forecastDate.Year, forecastDate.Month, 1).AddMonths(GraduationTargetMarginMonths + 1).AddDays(-1);
    }

    /// <param name="goals">Weekly/monthly study goals to seed with, unless the account already set
    /// its own (<see cref="HasDefaultStudyGoals"/>); when they are seeded, the rest of the current
    /// week and month is planned up to their minimums. Null keeps the account's goals and plans
    /// nothing extra.</param>
    public static async Task PopulateAsync(StudyLifeDb db, int userId, DateTime now,
        SampleDataLanguage language = SampleDataLanguage.German, SampleStudyGoals? goals = null)
    {
        var content = SampleDataContent.For(language);
        var rng = new Random(RandomSeed);
        var today = now.Date;

        // All or nothing: the English variant writes its program first (the rest needs the
        // generated course ids), so the whole dataset goes into one transaction. A caller that
        // already holds one keeps it.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync()
            : null;

        // ── Settings ─────────────────────────────────────────────────────────────
        // An existing account may already have its settings row (created on first use); only the
        // course selection and the program are set, the study goals and the graduation target only
        // where the account has none of its own, everything else keeps its value.
        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.AuthUserId == userId);
        if (settings is null)
        {
            // Entity defaults cover the rest (dark theme, 25-30h weekly goal, reminder config).
            settings = new UserSettingsEntity { AuthUserId = userId };
            db.Settings.Add(settings);
        }

        // German: the built-in catalog (no program row, ActiveStudyProgramId stays null).
        // English: the account's own copy of the program with English names and topics.
        var catalog = content.Program is null
            ? CourseCatalog.AppliedAICourses
            : await CreateProgramAsync(db, userId, content.Program, now, settings);

        var semester1 = catalog.Where(c => c.Semester == 1).ToList();
        // Active courses: semester 2 minus the project module (typical "project comes last" order).
        var active = catalog.Where(c => c.Semester == 2 && !c.Code.EndsWith("P")).ToList();

        settings.SelectedCourseIds = string.Join(",", active.Select(c => c.Id));
        settings.CompletedCourseIds = string.Join(",", semester1.Select(c => c.Id));
        // Goals the account set itself are never overwritten: the sample goals (and the plan up to
        // them further down) only replace the entity defaults.
        var planGoals = goals is not null && HasDefaultStudyGoals(settings) ? goals : null;
        if (planGoals is not null)
        {
            settings.WeeklyGoalMinHours = planGoals.WeeklyMinHours;
            settings.WeeklyGoalMaxHours = planGoals.WeeklyMaxHours;
            settings.MonthlyGoalMinHours = planGoals.MonthlyMinHours;
            settings.MonthlyGoalMaxHours = planGoals.MonthlyMaxHours;
        }

        // ── Completed semester 1: one goal row per course with grade + completion ──
        var grades = new[] { 1.3m, 1.7m, 2.0m, 2.3m, 1.0m };
        var semester1CompletedAt = new List<DateTime>();
        for (var i = 0; i < semester1.Count; i++)
        {
            var course = semester1[i];
            var completedAt = today.AddMonths(-6 + i).AddDays(-rng.Next(0, 12));
            semester1CompletedAt.Add(completedAt);
            db.CourseGoals.Add(new CourseGoalEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                TargetDate = today.AddMonths(-6 + i),
                CompletedAt = completedAt,
                Grade = grades[i % grades.Length],
                CompletedTopics = string.Join(",", course.Topics),
                CompletionNote = i == 0 ? content.CompletionNote : null,
            });
        }

        // Goals for two of the active courses (upcoming exam dates).
        db.CourseGoals.Add(new CourseGoalEntity
        {
            AuthUserId = userId,
            CourseId = active[0].Id,
            CourseName = active[0].Name,
            TargetDate = today.AddDays(24),
            CompletedTopics = string.Join(",", active[0].Topics.Take(3)),
        });
        db.CourseGoals.Add(new CourseGoalEntity
        {
            AuthUserId = userId,
            CourseId = active[1].Id,
            CourseName = active[1].Name,
            TargetDate = today.AddDays(45),
            CompletedTopics = string.Join(",", active[1].Topics.Take(1)),
        });

        // ── Study history: ~10 weeks of sessions on the active courses ───────────
        // Weekdays study more often than weekends; the last 12 days ALL have at least one
        // completed session so the dashboard shows a live, unbroken streak ending today. Today
        // itself is not part of this loop; it gets the fixed shape further down.
        var durations = new[] { 45, 60, 60, 90, 90, 120 };
        var history = new List<StudySessionEntity>();
        for (var dayOffset = 70; dayOffset >= 1; dayOffset--)
        {
            var day = today.AddDays(-dayOffset);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var mustStudy = dayOffset <= LiveStreakDays;
            if (!mustStudy && rng.NextDouble() > (isWeekend ? 0.4 : 0.8)) continue;

            var sessionsToday = mustStudy ? 1 + rng.Next(0, 2) : 1 + rng.Next(0, 3);
            var hour = 9 + rng.Next(0, 3);
            for (var s = 0; s < sessionsToday; s++)
            {
                var course = active[rng.Next(active.Count)];
                var minutes = durations[rng.Next(durations.Length)];
                var start = day.AddHours(hour).AddMinutes(15 * rng.Next(0, 4));
                var end = start.AddMinutes(minutes);
                history.Add(new StudySessionEntity
                {
                    AuthUserId = userId,
                    CourseId = course.Id,
                    CourseName = course.Name,
                    CourseColor = course.Color,
                    StartTime = start,
                    EndTime = end,
                    Topic = course.Topics[rng.Next(course.Topics.Count)],
                    IsCompleted = true,
                    TimerModeId = 1 + rng.Next(0, 3),
                });
                hour += minutes / 60 + 2; // gap before the next session
                if (hour >= 20) break;
            }
        }
        ShapeMonthTrend(history, today);
        db.Sessions.AddRange(history);
        db.Sessions.AddRange(EarlierSemesterHistory(userId, semester1, semester1CompletedAt, today));

        // ── Today: the day in progress ───────────────────────────────────────────
        // A session finished this morning (once "now" is past it, so nothing completed lies in
        // the future) and one still planned for later today: the dashboard's "Today" and "Up
        // next", the calendar's day view and the focus page then all show a day in progress
        // instead of an empty one. Fixed times instead of random ones, so the shape holds at
        // every reseed time (a demo restart at any hour, a screenshot run with a pinned clock).
        var ahead = new List<StudySessionEntity>();
        var morningStart = today.Add(TodaysCompletedStart);
        var morningEnd = morningStart.Add(TodaysCompletedLength);
        if (morningEnd <= now)
        {
            var course = active[0];
            ahead.Add(new StudySessionEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = morningStart,
                EndTime = morningEnd,
                Topic = course.Topics[rng.Next(course.Topics.Count)],
                IsCompleted = true,
                TimerModeId = 1,
            });
        }
        if (PlannedStartToday(now) is { } plannedToday)
        {
            var course = active[1 % active.Count];
            ahead.Add(new StudySessionEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = plannedToday,
                EndTime = plannedToday.AddMinutes(90),
                Topic = course.Topics[rng.Next(course.Topics.Count)],
                IsCompleted = false,
                TimerModeId = 1,
            });
        }

        // ── Planned sessions for the coming week ─────────────────────────────────
        foreach (var (dayAhead, courseIdx, startHour) in new[] { (1, 0, 18), (2, 1, 17), (4, 2, 10), (6, 0, 14) })
        {
            var course = active[courseIdx % active.Count];
            var start = today.AddDays(dayAhead).AddHours(startHour);
            ahead.Add(new StudySessionEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = start,
                EndTime = start.AddMinutes(90),
                Topic = course.Topics[rng.Next(course.Topics.Count)],
                IsCompleted = false,
                TimerModeId = 1,
            });
        }

        // ── Planned up to the study goals (when the sample goals were seeded) ────
        if (planGoals is not null)
        {
            ahead.AddRange(PlanUpToGoals([.. history, .. ahead], now, planGoals, (start, n) =>
            {
                var course = active[n % active.Count];
                return new StudySessionEntity
                {
                    AuthUserId = userId,
                    CourseId = course.Id,
                    CourseName = course.Name,
                    CourseColor = course.Color,
                    StartTime = start,
                    EndTime = start.Add(PlannedLength),
                    Topic = course.Topics[n % course.Topics.Count],
                    IsCompleted = false,
                    TimerModeId = 1,
                };
            }));
        }
        db.Sessions.AddRange(ahead);

        // ── Graduation target matching the seeded pace ───────────────────────────
        // A target date the account already set stays.
        // Elective-group quotas as the app applies them: the built-in catalog has its own, the
        // English sample program defines none.
        IReadOnlyDictionary<string, int> groupQuotas = content.Program is null
            ? CourseCatalog.GroupEctsQuotas
            : new Dictionary<string, int>();
        settings.TargetGraduationDate ??= GraduationTarget(catalog, groupQuotas, semester1.Select(c => c.Id),
            [.. history, .. ahead], settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, now);

        // ── A few notes tied to the active courses ───────────────────────────────
        foreach (var note in content.Notes)
        {
            var course = active[note.CourseIndex % active.Count];
            var created = today.AddDays(-rng.Next(3, 21)).AddHours(19);
            db.Notes.Add(new NoteEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                Title = note.Title,
                Content = note.Content,
                CreatedAt = created,
                UpdatedAt = created,
            });
        }

        await db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
    }

    /// <summary>
    /// Creates <paramref name="program"/> as the account's own study program, makes it the active
    /// one and hides the built-in program from the account's switcher (allowed because a custom
    /// program now exists - the invariant SettingsService.DismissBuiltInProgramAsync keeps).
    /// Returns the courses as the API shows them (ids shifted by
    /// <see cref="StudyProgramCatalog.CustomCourseIdOffset"/>).
    /// </summary>
    private static async Task<List<CourseDto>> CreateProgramAsync(
        StudyLifeDb db, int userId, SampleProgram program, DateTime now, UserSettingsEntity settings)
    {
        var programRow = new StudyProgramEntity { AuthUserId = userId, Name = program.Name, CreatedAt = now };
        db.StudyPrograms.Add(programRow);
        await db.SaveChangesAsync();

        var rows = program.Courses.Select(c => new CustomCourseEntity
        {
            AuthUserId = userId,
            StudyProgramId = programRow.Id,
            Semester = c.Semester,
            Name = c.Name,
            Code = c.Code,
            Color = c.Color,
            Icon = c.Icon,
            Ects = c.Ects,
            Topics = string.Join(",", c.Topics),
        }).ToList();
        db.CustomCourses.AddRange(rows);
        await db.SaveChangesAsync();

        settings.ActiveStudyProgramId = programRow.Id;
        settings.BuiltInProgramDismissed = true;

        return rows.Select(r => new CourseDto
        {
            Id = StudyProgramCatalog.CustomCourseIdOffset + r.Id,
            Semester = r.Semester,
            Name = r.Name,
            Code = r.Code,
            Color = r.Color,
            Icon = r.Icon,
            Ects = r.Ects,
            Topics = r.Topics.Split(',').ToList(),
        }).ToList();
    }
}
