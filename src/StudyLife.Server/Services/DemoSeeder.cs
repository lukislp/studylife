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
/// </summary>
public static class DemoSeeder
{
    public static async Task ReseedAsync(StudyLifeDb db)
    {
        // Content is deterministic (SampleDataset uses a seeded Random) - only the anchor date
        // moves, so two restarts on the same day produce the identical dataset.
        var now = DateTime.Now;
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
        await SampleDataset.PopulateAsync(db, user.Id, now);
    }
}
