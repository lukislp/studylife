using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Seed data for public demo instances (DEMO_MODE=true) - never runs on a normal deployment.
/// Called at every startup and wipes/re-creates ALL user data from scratch: dates are
/// generated relative to "today", so a container restart is also the refresh mechanism that
/// keeps the demo looking current (streak ends today, planned sessions lie in the coming
/// days) instead of slowly aging into "last studied 3 months ago". SystemSecrets are
/// deliberately left untouched (VAPID keys must stay stable across restarts).
///
/// The story told by the data: a student in the built-in "Applied Artificial Intelligence"
/// program, semester 1 fully completed (grades + ECTS progress), currently mid-semester-2
/// with an active study streak and a handful of planned sessions ahead. Courses/topics come
/// straight from CourseCatalog.AppliedAICourses - real names, colors, and topic lists, no
/// invented placeholder strings.
///
/// The demo user gets a CalendarToken (the ICS feed is a deliberately shown-off feature;
/// the token only ever grants GET /api/sessions/ics - verified against the gate) but NO
/// ApiKeyHash/AiApiKeyHash/McpApiKeyHash: API keys (any slot) are meant to be completely
/// unusable on a demo instance, and with a null hash every submitted key fails the gate with
/// 401. Generating one is a POST and therefore blocked by the demo write-block middleware anyway.
/// </summary>
public static class DemoSeeder
{
    public static async Task ReseedAsync(StudyLifeDb db)
    {
        // Deterministic content (same courses, topics, time slots) on every reseed - only the
        // anchor date moves. Seeded Random instead of Random.Shared so two restarts on the
        // same day produce the identical dataset.
        var rng = new Random(20260805);
        var today = DateTime.Now.Date;
        var now = DateTime.Now;

        // ── Wipe all user data (order: dependents first, then users) ─────────────
        // IgnoreQueryFilters is essential on every multi-tenant table: this runs from the
        // Program.cs startup block with no HTTP context and no BeginBackgroundScope, so the
        // global AuthUserId query filters would resolve to user 0 and silently delete
        // NOTHING - every demo restart would then orphan the previous dataset and stack a
        // fresh one on top instead of wiping (caught by DemoSeederTests' reseed assertions).
        await db.Sessions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Notes.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.CourseGoals.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.TimerState.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Settings.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.PushSubscriptions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.SentReminders.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.SessionTemplates.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.CourseResources.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.CustomCourses.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.CourseGroups.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.StudyPrograms.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.AuthSessions.ExecuteDeleteAsync();
        await db.PasskeyCredentials.ExecuteDeleteAsync();
        await db.RecoveryCodes.ExecuteDeleteAsync();
        await db.AuthUsers.ExecuteDeleteAsync();

        // ── Demo user ────────────────────────────────────────────────────────────
        var user = new AuthUserEntity
        {
            DisplayName = "Demo",
            CreatedAt = today.AddMonths(-8),
            CalendarToken = AuthSessionService.GenerateToken(),
            CalendarTokenCreatedAt = now,
            ApiKeyHash = null, // see class summary - API keys must stay unusable on the demo
            AiApiKeyHash = null,
            McpApiKeyHash = null,
            // Set explicitly instead of relying on OwnershipService's self-heal fallback: the sole
            // demo user is always the "first/only" user by definition, and setting it directly
            // avoids a self-heal warning log on every container restart. account-info's isOwner
            // must stay stable across reseeds (it feeds the setup UI) even though the demo user's
            // Id itself changes every restart - see AuthControllerEdgeTests's demo owner test.
            IsOwner = true,
        };
        db.AuthUsers.Add(user);
        await db.SaveChangesAsync(); // materialize user.Id for all rows below

        var catalog = CourseCatalog.AppliedAICourses;
        var semester1 = catalog.Where(c => c.Semester == 1).ToList();
        // Active courses: semester 2 minus the project module (typical "project comes last" order).
        var active = catalog.Where(c => c.Semester == 2 && !c.Code.EndsWith("P")).ToList();

        // ── Settings ─────────────────────────────────────────────────────────────
        db.Settings.Add(new UserSettingsEntity
        {
            AuthUserId = user.Id,
            SelectedCourseIds = string.Join(",", active.Select(c => c.Id)),
            CompletedCourseIds = string.Join(",", semester1.Select(c => c.Id)),
            TargetGraduationDate = today.AddYears(2).AddMonths(3),
            // Entity defaults cover the rest (dark theme, 25-30h weekly goal, reminder config).
        });

        // ── Completed semester 1: one goal row per course with grade + completion ──
        var grades = new[] { 1.3m, 1.7m, 2.0m, 2.3m, 1.0m };
        for (var i = 0; i < semester1.Count; i++)
        {
            var course = semester1[i];
            db.CourseGoals.Add(new CourseGoalEntity
            {
                AuthUserId = user.Id,
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
            AuthUserId = user.Id,
            CourseId = active[0].Id,
            CourseName = active[0].Name,
            TargetDate = today.AddDays(24),
            CompletedTopics = string.Join(",", active[0].Topics.Take(3)),
        });
        db.CourseGoals.Add(new CourseGoalEntity
        {
            AuthUserId = user.Id,
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
                    AuthUserId = user.Id,
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
                AuthUserId = user.Id,
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
                AuthUserId = user.Id,
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
