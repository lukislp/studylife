// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.SampleData;

/// <summary>
/// The sample study dataset, written into ONE existing account: a student in the built-in
/// "Applied Artificial Intelligence" program, semester 1 fully completed (grades + ECTS
/// progress), currently mid-semester-2 with an active study streak ending today, a handful of
/// planned sessions in the coming days, two upcoming exam goals and a few notes. Courses and
/// topics come straight from CourseCatalog.AppliedAICourses - real names, colors and topic
/// lists, no invented placeholder strings.
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

    public static async Task PopulateAsync(StudyLifeDb db, int userId, DateTime now)
    {
        var rng = new Random(RandomSeed);
        var today = now.Date;

        var catalog = CourseCatalog.AppliedAICourses;
        var semester1 = catalog.Where(c => c.Semester == 1).ToList();
        // Active courses: semester 2 minus the project module (typical "project comes last" order).
        var active = catalog.Where(c => c.Semester == 2 && !c.Code.EndsWith("P")).ToList();

        // ── Settings ─────────────────────────────────────────────────────────────
        // An existing account may already have its settings row (created on first use); only the
        // course selection and the graduation target are set, everything else keeps its value.
        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.AuthUserId == userId);
        if (settings is null)
        {
            // Entity defaults cover the rest (dark theme, 25-30h weekly goal, reminder config).
            settings = new UserSettingsEntity { AuthUserId = userId };
            db.Settings.Add(settings);
        }
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
                CompletionNote = i == 0 ? "Klausur lief besser als erwartet." : null,
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
        // completed session so the dashboard shows a live, unbroken streak ending today.
        var durations = new[] { 45, 60, 60, 90, 90, 120 };
        for (var dayOffset = 70; dayOffset >= 0; dayOffset--)
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
                // Today's session(s): only count as far as "now", so the day looks in-progress
                // rather than containing sessions from the future.
                if (day == today && end > now) break;
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
        var notes = new (int CourseIdx, string Title, string Content)[]
        {
            (0, "Merkzettel Eigenwerte", "Eigenwerte über det(A - λI) = 0 bestimmen.\nEigenvektoren: (A - λI)x = 0 lösen.\nWichtig für die Klausur: Diagonalisierbarkeit prüfen!"),
            (1, "Verteilungen Übersicht", "Binomial: diskret, n Versuche, Trefferwahrscheinlichkeit p.\nNormal: stetig, μ und σ².\nPoisson: seltene Ereignisse pro Intervall.\nFaustregel: n·p > 5 und n·(1-p) > 5 → Normalapproximation ok."),
            (2, "Fragen an Tutor", "- Unterschied Konfidenzintervall vs. Prognoseintervall nochmal durchgehen\n- Übungsblatt 4, Aufgabe 3b\n- Klausurzulassung: reicht das Testat?"),
        };
        foreach (var (courseIdx, title, content) in notes)
        {
            var course = active[courseIdx % active.Count];
            var created = today.AddDays(-rng.Next(3, 21)).AddHours(19);
            db.Notes.Add(new NoteEntity
            {
                AuthUserId = userId,
                CourseId = course.Id,
                Title = title,
                Content = content,
                CreatedAt = created,
                UpdatedAt = created,
            });
        }

        await db.SaveChangesAsync();
    }
}
