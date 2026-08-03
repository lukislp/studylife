using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // internal instead of private + a pure function (date/resource count -> text), so
    // BackgroundTaskServiceTests can check the resource addendum (feature 3: course resource
    // link in the session reminder) directly - the actual push payload is otherwise never
    // visible in plaintext anywhere (WebPushClient encrypts before sending). Same pattern as
    // PickDailyMotivationQuote in BackgroundTaskService.Motivation.cs.
    internal static string BuildSessionReminderBody(string courseName, string? topic, DateTime startTime, int resourceCount)
    {
        var body = $"{courseName}{(topic != null ? $": {topic}" : "")} um {startTime:HH:mm} Uhr";
        if (resourceCount > 0)
            body += $" — {resourceCount} Ressource{(resourceCount == 1 ? "" : "n")} für diesen Kurs hinterlegt";
        return body;
    }

    // internal instead of private: allows StudyLife.Server.Tests (InternalsVisibleTo in the csproj)
    // to call the method directly instead of only reaching it indirectly via the 30s
    // ExecuteAsync loop - see BackgroundTaskServiceTests.
    internal async Task RunPushNotificationsAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.SessionRemindersEnabled) return;
        var ReminderMinutes = ReminderSettings.ParseSessionReminderMinutes(settings?.SessionReminderMinutes);

        var now = DateTime.Now;

        // Load all sessions starting within the next 61 minutes (not yet started)
        var upcomingSessions = await db.Sessions
            .Where(s => !s.IsCompleted
                && s.StartTime > now
                && s.StartTime <= now.AddMinutes(ReminderMinutes.Max() + 1))
            .ToListAsync();

        _logger.LogDebug("PushCheck {Now:HH:mm:ss}: {Count} Session(s) im Fenster", now, upcomingSessions.Count);

        if (!upcomingSessions.Any()) return;

        var subscriptions = await getSubscriptions();
        _logger.LogDebug("Aktive Push-Subscriptions: {Count}", subscriptions.Count);
        if (!subscriptions.Any()) return;

        // Pre-load the resource count per affected course (instead of querying per session/reminder
        // individually) - appends a short addendum to the push text if resources are already
        // stored for the course (CourseResourcesController). Purely additive, no separate toggle.
        var upcomingCourseIds = upcomingSessions.Select(s => s.CourseId).Distinct().ToList();
        var resourceCountsByCourseId = await db.CourseResources
            .Where(r => upcomingCourseIds.Contains(r.CourseId))
            .GroupBy(r => r.CourseId)
            .Select(g => new { CourseId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CourseId, x => x.Count);

        // Load already-sent reminder keys from the DB
        var sessionIds = upcomingSessions.Select(s => s.Id).ToList();
        var expectedKeys = sessionIds
            .SelectMany(id => ReminderMinutes.Select(r => $"{id}:reminder{r}"))
            .ToList();
        var sentKeys = await db.SentReminders
            .Where(r => expectedKeys.Contains(r.Key))
            .Select(r => r.Key)
            .ToHashSetAsync();

        var client = GetPushClient();

        bool dbChanged = false;

        foreach (var session in upcomingSessions)
        {
            var minutesUntil = (session.StartTime - now).TotalMinutes;

            _logger.LogInformation("Session {Id} '{Name}' startet in {Min:F1} min", session.Id, session.CourseName, minutesUntil);

            // All not-yet-sent, already-due thresholds for this session, sorted with the
            // nearest first. Normally exactly one (the 30s tick passes thresholds one at a
            // time) - but if the poller was offline for longer (redeploy, Pi reboot) and a
            // session slips past several thresholds at once, this would otherwise be up to
            // ReminderMinutes.Count pushes at once for the same session.
            var dueThresholds = ReminderMinutes
                .Where(r => minutesUntil <= r && !sentKeys.Contains($"{session.Id}:reminder{r}"))
                .OrderBy(r => r)
                .ToList();
            if (dueThresholds.Count == 0) continue;

            // Mark all but the nearest threshold as "sent" only, without pushing -
            // due to the outage they can no longer be delivered on time anyway;
            // a single push for the most urgent remaining threshold is all the
            // user still needs now.
            foreach (var skipped in dueThresholds.Skip(1))
            {
                var skippedKey = $"{session.Id}:reminder{skipped}";
                if (await TryClaimReminderAsync(db, skippedKey, now)) sentKeys.Add(skippedKey);
            }

            var reminderAt = dueThresholds[0];
            var key = $"{session.Id}:reminder{reminderAt}";
            // Claim BEFORE sending: if another worker wins the same session/threshold first,
            // nothing gets sent here - no duplicate push for the same session.
            if (!await TryClaimReminderAsync(db, key, now)) continue;
            sentKeys.Add(key);

            var title = reminderAt switch
            {
                60 => "Lernphase in 1 Stunde 📚",
                30 => "Lernphase in 30 Minuten ⏰",
                1 => "Lernphase in 1 Minute! 🔔",
                _ => $"Lernphase in {reminderAt} Minuten ⏰"
            };
            resourceCountsByCourseId.TryGetValue(session.CourseId, out var resourceCount);
            var body = BuildSessionReminderBody(session.CourseName, session.Topic, session.StartTime, resourceCount);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

            _logger.LogInformation("Sende Reminder '{Key}': {Title}", key, title);

            var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Push failed for {Endpoint}")));

            foreach (var result in results)
            {
                if (!result.Expired) continue;
                db.PushSubscriptions.Remove(result.Subscription);
                dbChanged = true;
                _logger.LogInformation("Abgelaufene Subscription entfernt: {Endpoint}", result.Subscription.Endpoint);
            }
        }

        // Clean up old reminder entries (older than 2 days). Achievement and course-goal keys
        // are deliberately excluded: achievements deduplicate permanently, and course-goal
        // reminders fire via threshold comparison (daysUntil <= reminderAt) - if their key were
        // deleted after 2 days, e.g. the 14-day reminder would fire again every 2 days as long
        // as the target date is within the window. Only session (window max. 61 min), inactivity,
        // and motivation keys (both deduplicated per calendar day, daily re-firing is intended
        // there) age out.
        var cutoff = now.AddDays(-2);
        var old = await db.SentReminders
            .Where(r => r.SentAt < cutoff
                && !r.Key.StartsWith("achievement:")
                && !r.Key.StartsWith("coursegoal:"))
            .ToListAsync();
        if (old.Any()) { db.SentReminders.RemoveRange(old); dbChanged = true; }

        if (dbChanged)
            await db.SaveChangesAsync();
    }

    internal async Task RunCourseGoalReminderCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.CourseGoalRemindersEnabled) return;
        var ReminderDays = ReminderSettings.ParseCourseGoalReminderDays(settings?.CourseGoalReminderDays);

        var today = DateTime.Now.Date;

        var goals = await db.CourseGoals
            .Where(g => g.TargetDate != null && g.CompletedAt == null)
            .ToListAsync();

        if (!goals.Any()) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        var expectedKeys = goals
            .SelectMany(g => ReminderDays.Select(d => $"coursegoal:{g.CourseId}:reminder{d}d"))
            .ToList();
        var sentKeys = await db.SentReminders
            .Where(r => expectedKeys.Contains(r.Key))
            .Select(r => r.Key)
            .ToHashSetAsync();

        var client = GetPushClient();
        bool dbChanged = false;

        foreach (var goal in goals)
        {
            var daysUntil = (goal.TargetDate!.Value.Date - today).Days;

            foreach (var reminderAt in ReminderDays.OrderByDescending(x => x))
            {
                if (daysUntil > reminderAt) continue;

                var key = $"coursegoal:{goal.CourseId}:reminder{reminderAt}d";
                if (sentKeys.Contains(key)) continue;
                if (!await TryClaimReminderAsync(db, key, DateTime.Now)) { sentKeys.Add(key); continue; }
                sentKeys.Add(key);

                var title = reminderAt switch
                {
                    0 => "Kursziel heute fällig 🎯",
                    1 => "Kursziel morgen fällig ⏳",
                    _ => $"Kursziel in {reminderAt} Tagen fällig 🎯"
                };
                var body = $"{goal.CourseName}: Ziel-Datum {goal.TargetDate:dd.MM.yyyy}";
                var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

                var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Course goal push failed for {Endpoint}")));

                foreach (var result in results)
                {
                    if (!result.Expired) continue;
                    db.PushSubscriptions.Remove(result.Subscription);
                    dbChanged = true;
                }
            }
        }

        if (dbChanged)
            await db.SaveChangesAsync();
    }

    internal async Task RunInactivityReminderCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.InactivityRemindersEnabled) return;
        var InactivityThresholdDays = ReminderSettings.GetInactivityThresholdDays(settings?.InactivityThresholdDays ?? 0);

        var now = DateTime.Now;
        var today = now.Date;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        var lastPastSession = await db.Sessions
            .Where(s => s.StartTime <= now)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        int daysSince;
        if (lastPastSession == null)
        {
            // Never had a session before - treat as "inactive" anyway,
            // daysSince here is only informational for the notification text.
            daysSince = InactivityThresholdDays;
        }
        else
        {
            daysSince = (today - lastPastSession.StartTime.Date).Days;
        }

        if (lastPastSession != null && daysSince <= InactivityThresholdDays) return;

        var key = $"inactivity:{today:yyyyMMdd}";
        var alreadySent = await db.SentReminders.AnyAsync(r => r.Key == key);
        if (alreadySent) return;
        if (!await TryClaimReminderAsync(db, key, DateTime.Now)) return;

        var title = "Lange nichts gelernt? 📚";
        var body = $"Seit {daysSince} Tagen keine Lernsession - Zeit für eine neue Runde!";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Inactivity push failed for {Endpoint}")));

        foreach (var result in results)
        {
            if (!result.Expired) continue;
            db.PushSubscriptions.Remove(result.Subscription);
            dbChanged = true;
        }

        if (dbChanged)
            await db.SaveChangesAsync();
    }

    /// <summary>
    /// Complements RunInactivityReminderCheckAsync with the case "active overall, but hasn't
    /// touched ONE course for a while" - the global reminder only fires on
    /// complete radio silence and would never catch this case.
    /// </summary>
    internal async Task RunPerCourseInactivityCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings == null || !settings.PerCourseInactivityRemindersEnabled) return;
        var InactivityThresholdDays = ReminderSettings.GetInactivityThresholdDays(settings.InactivityThresholdDays);

        var courseIds = (settings.SelectedCourseIds ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();
        if (!courseIds.Any()) return;

        var now = DateTime.Now;
        var today = now.Date;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        // Reference point "active anyway": the most recently started session across ALL courses.
        // If there isn't one at all, the global inactivity reminder applies instead.
        var lastAnySession = await db.Sessions
            .Where(s => s.StartTime <= now)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (lastAnySession == null) return;

        var client = GetPushClient();
        bool dbChanged = false;

        foreach (var courseId in courseIds)
        {
            var lastCourseSession = await db.Sessions
                .Where(s => s.CourseId == courseId && s.StartTime <= now)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();
            // Never started -> "neglected" doesn't fit here, that's a different case.
            if (lastCourseSession == null) continue;

            var daysSinceCourse = (today - lastCourseSession.StartTime.Date).Days;
            if (daysSinceCourse <= InactivityThresholdDays) continue;

            // Must have had a DIFFERENT, newer session since then - otherwise it's simply
            // the overall oldest session and thus a case for the global reminder.
            if (lastAnySession.StartTime <= lastCourseSession.StartTime) continue;

            var key = $"courseinactivity:{courseId}:{today:yyyyMMdd}";
            var alreadySent = await db.SentReminders.AnyAsync(r => r.Key == key);
            if (alreadySent) continue;
            if (!await TryClaimReminderAsync(db, key, now)) continue;

            var title = $"Kurs {lastCourseSession.CourseName} etwas vernachlässigt? 📚";
            var body = $"Seit {daysSinceCourse} Tagen keine Session mehr in {lastCourseSession.CourseName}, obwohl du sonst weiterlernst.";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

            var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Course inactivity push failed for {Endpoint}")));

            foreach (var result in results)
            {
                if (!result.Expired) continue;
                db.PushSubscriptions.Remove(result.Subscription);
                dbChanged = true;
            }
        }

        if (dbChanged)
            await db.SaveChangesAsync();
    }
}
