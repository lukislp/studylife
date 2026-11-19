// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Deletes rows that have outlived their purpose (GDPR audit F-15): expired login sessions, used
/// or expired invites, spent recovery codes, never-approved additional passkeys, push
/// subscriptions of long-inactive accounts, achievement markers that no category produces any
/// more, aged session/inactivity/motivation reminder markers and course-goal markers whose goal
/// can no longer fire them. Instance-wide (all users in one pass, hence IgnoreQueryFilters), idempotent, and driven by
/// <see cref="RetentionOptions"/>; BackgroundTaskService runs it once a day.
/// </summary>
public sealed class DataRetentionCleaner(RetentionOptions options)
{
    /// <summary>How many rows each rule removed - what the worker logs.</summary>
    public sealed record Result(
        int Sessions, int Invites, int RecoveryCodes, int Passkeys, int PushSubscriptions, int Reminders)
    {
        public int Total => Sessions + Invites + RecoveryCodes + Passkeys + PushSubscriptions + Reminders;
    }

    public async Task<Result> RunAsync(StudyLifeDb db, DateTime utcNow, CancellationToken ct = default)
    {
        var sessions = await db.AuthSessions.IgnoreQueryFilters()
            .Where(s => s.ExpiresAt <= utcNow || s.HardExpiresAt <= utcNow)
            .ExecuteDeleteAsync(ct);

        var inviteCutoff = utcNow.AddDays(-options.InviteDays);
        var invites = await db.AuthInvites.IgnoreQueryFilters()
            .Where(i => (i.UsedAt != null && i.UsedAt < inviteCutoff)
                || (i.UsedAt == null && i.ExpiresAt < inviteCutoff))
            .ExecuteDeleteAsync(ct);

        var recoveryCutoff = utcNow.AddDays(-options.UsedRecoveryCodeDays);
        var recoveryCodes = await db.RecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UsedAt != null && c.UsedAt < recoveryCutoff)
            .ExecuteDeleteAsync(ct);

        var passkeyCutoff = utcNow.AddDays(-options.UnapprovedPasskeyDays);
        var passkeys = await db.PasskeyCredentials.IgnoreQueryFilters()
            .Where(p => p.ApprovedAt == null && p.CreatedAt < passkeyCutoff)
            .ExecuteDeleteAsync(ct);

        var pushCutoff = utcNow.AddDays(-options.InactivePushSubscriptionDays);
        var pushSubscriptions = await db.PushSubscriptions.IgnoreQueryFilters()
            .Where(p => (p.CreatedAt == null || p.CreatedAt < pushCutoff)
                && !db.AuthSessions.IgnoreQueryFilters()
                    .Any(s => s.AuthUserId == p.AuthUserId && s.LastUsedAt >= pushCutoff))
            .ExecuteDeleteAsync(ct);

        // "achievement:{category}:{tier}" markers deduplicate pushes permanently, so a marker of a
        // category the worker still pushes must stay (deleting it would re-fire the push). Only
        // markers of categories that no longer exist are stale.
        var reminders = await db.SentReminders.IgnoreQueryFilters()
            .Where(r => r.Key.StartsWith("achievement:")
                && !r.Key.StartsWith("achievement:" + AchievementCatalog.HoursKey + ":")
                && !r.Key.StartsWith("achievement:" + AchievementCatalog.StreakKey + ":")
                && !r.Key.StartsWith("achievement:" + AchievementCatalog.SessionsKey + ":")
                && !r.Key.StartsWith("achievement:" + AchievementCatalog.CoursesKey + ":")
                && r.Key != "achievement:" + AchievementCatalog.AllCoursesKey)
            .ExecuteDeleteAsync(ct);

        // Session, inactivity and motivation markers age out by time, for every user (the worker's
        // own reminder runs return early for accounts without a push subscription or an upcoming
        // session and used to be the only place that deleted them). SentAt is written in server
        // local time; a difference of hours against a cutoff of days does not matter.
        var markerCutoff = utcNow.AddDays(-options.ReminderMarkerDays);
        reminders += await db.SentReminders.IgnoreQueryFilters()
            .Where(r => r.SentAt < markerCutoff
                && !r.Key.StartsWith("achievement:")
                && !r.Key.StartsWith("coursegoal:"))
            .ExecuteDeleteAsync(ct);

        reminders += await DeleteOrphanedCourseGoalMarkersAsync(db, ct);

        return new Result(sessions, invites, recoveryCodes, passkeys, pushSubscriptions, reminders);
    }

    /// <summary>
    /// "coursegoal:{courseId}:reminder{n}d" markers deduplicate the goal reminders for as long as the
    /// goal can still fire them (they compare "days until the target date &lt;= n", so deleting a
    /// marker of an open goal would send the reminder again every day). Once the goal is deleted,
    /// completed or has no target date any more nothing can fire, and the marker only remains as a
    /// trace of the account's study planning. Keys that do not parse are left alone.
    /// </summary>
    private static async Task<int> DeleteOrphanedCourseGoalMarkersAsync(StudyLifeDb db, CancellationToken ct)
    {
        var markers = await db.SentReminders.IgnoreQueryFilters()
            .Where(r => r.Key.StartsWith(CourseGoalMarkerPrefix))
            .Select(r => new { r.Id, r.AuthUserId, r.Key })
            .ToListAsync(ct);
        if (markers.Count == 0) return 0;

        var open = (await db.CourseGoals.IgnoreQueryFilters()
                .Where(g => g.TargetDate != null && g.CompletedAt == null)
                .Select(g => new { g.AuthUserId, g.CourseId })
                .ToListAsync(ct))
            .Select(g => (g.AuthUserId, g.CourseId))
            .ToHashSet();

        var orphaned = markers
            .Where(m => TryParseCourseId(m.Key, out var courseId) && !open.Contains((m.AuthUserId, courseId)))
            .Select(m => m.Id)
            .ToList();

        var deleted = 0;
        foreach (var batch in orphaned.Chunk(500))
            deleted += await db.SentReminders.IgnoreQueryFilters().Where(r => batch.Contains(r.Id)).ExecuteDeleteAsync(ct);
        return deleted;
    }

    private const string CourseGoalMarkerPrefix = "coursegoal:";

    private static bool TryParseCourseId(string key, out int courseId)
    {
        var parts = key.Split(':');
        courseId = 0;
        return parts.Length >= 2 && int.TryParse(parts[1], out courseId);
    }
}
