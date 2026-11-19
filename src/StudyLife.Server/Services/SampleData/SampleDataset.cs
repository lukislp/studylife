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

    public static async Task PopulateAsync(StudyLifeDb db, int userId, DateTime now,
        SampleDataLanguage language = SampleDataLanguage.German)
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
        // course selection, the program and the graduation target are set, everything else keeps
        // its value.
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
        settings.TargetGraduationDate = today.AddYears(2).AddMonths(3);

        // ── Completed semester 1: one goal row per course with grade + completion ──
        var grades = new[] { 1.3m, 1.7m, 2.0m, 2.3m, 1.0m };
        for (var i = 0; i < semester1.Count; i++)
        {
            var course = semester1[i];
            db.CourseGoals.Add(new CourseGoalEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                CourseName = course.Name,
                TargetDate = today.AddMonths(-6 + i),
                CompletedAt = today.AddMonths(-6 + i).AddDays(-rng.Next(0, 12)),
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
        for (var dayOffset = 70; dayOffset >= 1; dayOffset--)
        {
            var day = today.AddDays(-dayOffset);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var mustStudy = dayOffset <= 12; // live streak
            if (!mustStudy && rng.NextDouble() > (isWeekend ? 0.4 : 0.8)) continue;

            var sessionsToday = mustStudy ? 1 + rng.Next(0, 2) : 1 + rng.Next(0, 3);
            var hour = 9 + rng.Next(0, 3);
            for (var s = 0; s < sessionsToday; s++)
            {
                var course = active[rng.Next(active.Count)];
                var minutes = durations[rng.Next(durations.Length)];
                var start = day.AddHours(hour).AddMinutes(15 * rng.Next(0, 4));
                var end = start.AddMinutes(minutes);
                db.Sessions.Add(new StudySessionEntity
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

        // ── Today: the day in progress ───────────────────────────────────────────
        // A session finished this morning (once "now" is past it, so nothing completed lies in
        // the future) and one still planned for later today: the dashboard's "Today" and "Up
        // next", the calendar's day view and the focus page then all show a day in progress
        // instead of an empty one. Fixed times instead of random ones, so the shape holds at
        // every reseed time (a demo restart at any hour, a screenshot run with a pinned clock).
        var morningStart = today.Add(TodaysCompletedStart);
        var morningEnd = morningStart.Add(TodaysCompletedLength);
        if (morningEnd <= now)
        {
            var course = active[0];
            db.Sessions.Add(new StudySessionEntity
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
            db.Sessions.Add(new StudySessionEntity
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
            db.Sessions.Add(new StudySessionEntity
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
