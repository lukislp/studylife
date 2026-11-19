// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services.SampleData;

namespace StudyLife.Server.Services;

/// <summary>
/// Seed data for public demo instances (DEMO_MODE=true) - never runs on a normal deployment.
/// Called at every startup and wipes/re-creates ALL user data from scratch: dates are
/// generated relative to "today", so a container restart is also the refresh mechanism that
/// keeps the demo looking current (streak ends today, planned sessions lie in the coming
/// days) instead of slowly aging into "last studied 3 months ago". SystemSecrets are
/// deliberately left untouched (VAPID keys must stay stable across restarts).
///
/// The data itself (courses, goals, ~10 weeks of sessions, planned sessions, notes) is
/// <see cref="SampleDataset"/>, shared with <see cref="SampleAccountSeeder"/>; this class owns
/// only the demo-specific part: the full wipe and the single demo user.
///
/// The demo user gets a calendar link (CalendarTokenHash of a random token nobody is shown -
/// tokens are stored only as hashes, so the setup card shows the "link active, regenerate to
/// get a new one" state, and regenerating is a POST the write-block refuses; the token would
/// only ever grant GET /api/sessions/ics - verified against the gate) but NO
/// ApiKeyHash/AiApiKeyHash/McpApiKeyHash: API keys (any slot) are meant to be completely
/// unusable on a demo instance, and with a null hash every submitted key fails the gate with
/// 401. Generating one is a POST and therefore blocked by the demo write-block middleware anyway.
///
/// Two optional settings (<see cref="Configuration.DemoOptions"/>) shape the dataset: its
/// language (<c>Demo:Language</c>, German by default) and the time of day it is seeded for
/// (<c>Demo:Clock</c>, see <see cref="SeedTime"/>).
/// </summary>
public static class DemoSeeder
{
    /// <summary>The moment the dataset is seeded for: <paramref name="now"/> itself, or today at
    /// <paramref name="clock"/> ("HH:mm", 24-hour) when one is set. A store screenshot run pins the
    /// app's clock to such a time; seeding for the same time keeps "done this morning" and "planned
    /// for later today" on the right side of the time the screenshots show, whenever the run
    /// happens. Anything but a valid 24-hour time stops the start, so a typo cannot silently seed
    /// for the wrong time.</summary>
    public static DateTime SeedTime(DateTime now, string? clock)
    {
        if (string.IsNullOrWhiteSpace(clock))
            return now;
        if (!TimeOnly.TryParseExact(clock.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
            throw new InvalidOperationException($"Demo:Clock must be a 24-hour time like 09:41 (got \"{clock}\").");
        return now.Date.Add(time.ToTimeSpan());
    }

    /// <summary>Wipes all user data and seeds the demo user with the sample dataset, every date
    /// relative to <paramref name="now"/> (normally the current time, see <see cref="SeedTime"/>).</summary>
    public static async Task ReseedAsync(StudyLifeDb db, DateTime now,
        SampleDataLanguage language = SampleDataLanguage.German)
    {
        // Content is deterministic (SampleDataset uses a seeded Random) - only the anchor date
        // moves, so two restarts on the same day produce the identical dataset.
        var today = now.Date;

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
        await db.PasswordCredentials.ExecuteDeleteAsync();
        await db.AuthUsers.ExecuteDeleteAsync();

        // ── Demo user ────────────────────────────────────────────────────────────
        var user = new AuthUserEntity
        {
            DisplayName = "Demo",
            CreatedAt = today.AddMonths(-8),
            CalendarTokenHash = AuthSessionService.HashToken(AuthSessionService.GenerateToken()),
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

        // ── The sample dataset itself (shared with SampleAccountSeeder) ──────────
        await SampleDataset.PopulateAsync(db, user.Id, now, language);
    }
}
