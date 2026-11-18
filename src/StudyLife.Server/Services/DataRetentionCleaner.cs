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
/// subscriptions of long-inactive accounts and achievement markers that no category produces any
/// more. Instance-wide (all users in one pass, hence IgnoreQueryFilters), idempotent, and driven by
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

        return new Result(sessions, invites, recoveryCodes, passkeys, pushSubscriptions, reminders);
    }
}
