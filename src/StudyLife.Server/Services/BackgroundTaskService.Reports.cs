using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // internal instead of private: allows StudyLife.Server.Tests (InternalsVisibleTo in the csproj)
    // to call the method directly instead of only reaching it indirectly via the 30s
    // ExecuteAsync loop - see BackgroundTaskServiceTests.
    internal async Task RunAchievementCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        // Check the (memoized) subscriptions first: without recipients, the full scan
        // of the sessions table isn't worth it. Already-earned badges are then also not
        // marked as sent - consistent with the other sub-tasks, which likewise bail out
        // early without subscriptions.
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.AchievementNotificationsEnabled) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        var now = DateTime.Now;

        // Deliberately duplicates the client logic from Index.razor (BuildAchievements) - an
        // established pattern in this codebase (cf. Home Assistant integration): the server must
        // know the same milestones so the push still fires when the app isn't currently open.
        // "Studied" = timer completed OR scheduled end lies in the past -
        // the same semantics as /api/sessions/history?onlyCompleted=true, which the client uses.
        var studied = await db.Sessions
            .Where(s => s.IsCompleted || s.EndTime <= now)
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();

        var totalHours = studied.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = studied.Count;
        var longestStreak = StudyMetrics.CalcLongestStreak(studied.Select(s => s.StartTime));

        var completedIds = (settings?.CompletedCourseIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => int.TryParse(x, out _))
            .Select(int.Parse)
            .ToList();
        var coursesCompleted = completedIds.Count;

        // "All courses done" is based on creditable ECTS rather than a simple course count,
        // because elective groups only count up to the group quota - exactly how the client
        // computes it (CalcTotalEcts/CalcEctsEarned on the same shared catalog). Program-aware:
        // if a custom study program is active, its catalog + quotas count,
        // otherwise the built-in one as before.
        List<CourseDto> catalog;
        IReadOnlyDictionary<string, int> groupQuotas;
        if (settings?.ActiveStudyProgramId is int programId)
        {
            catalog = await StudyProgramCatalog.LoadCoursesAsync(db, programId);
            groupQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(db, programId);
        }
        else
        {
            catalog = CourseCatalog.AppliedAICourses;
            groupQuotas = CourseCatalog.GroupEctsQuotas;
        }
        var ectsTotal = CourseCatalog.CalcTotalEcts(catalog, groupQuotas);
        var ectsEarned = CourseCatalog.CalcEctsEarned(catalog, completedIds, groupQuotas);
        var allCoursesDone = ectsTotal > 0 && ectsEarned >= ectsTotal;

        // Thresholds exactly as in Index.razor (BuildAchievements) - keep in sync if changed there.
        var earned = new List<(string Key, string Body)>();
        foreach (var t in new[] { 25, 100, 500 })
            if (totalHours >= t) earned.Add(($"achievement:hours:{t}", $"{t} Stunden gelernt ⏱"));
        foreach (var t in new[] { 7, 30, 100 })
            if (longestStreak >= t) earned.Add(($"achievement:streak:{t}", $"{t}-Tage-Streak erreicht 🔥"));
        foreach (var t in new[] { 50, 200, 500 })
            if (totalSessions >= t) earned.Add(($"achievement:sessions:{t}", $"{t} Sessions abgeschlossen ✅"));
        foreach (var t in new[] { 1, 10, 20 })
            if (coursesCompleted >= t) earned.Add(($"achievement:courses:{t}", t == 1 ? "Ersten Kurs abgeschlossen 🎓" : $"{t} Kurse abgeschlossen 🎓"));
        if (allCoursesDone) earned.Add(("achievement:allcourses", "Alle Kurse abgeschlossen 🏆"));

        if (earned.Count == 0) return;

        var expectedKeys = earned.Select(e => e.Key).ToList();
        var sentKeys = await db.SentReminders
            .Where(r => expectedKeys.Contains(r.Key))
            .Select(r => r.Key)
            .ToHashSetAsync();
        var toSend = earned.Where(e => !sentKeys.Contains(e.Key)).ToList();
        if (toSend.Count == 0) return;

        var client = GetPushClient();
        bool dbChanged = false;

        foreach (var (key, body) in toSend)
        {
            if (!await TryClaimReminderAsync(db, key, now)) continue;

            var payload = System.Text.Json.JsonSerializer.Serialize(new { title = "Achievement freigeschaltet! 🏆", body });

            _logger.LogInformation("Sende Achievement-Push '{Key}': {Body}", key, body);

            var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Achievement push failed for {Endpoint}")));

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

    internal async Task RunWeeklyReportAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        // DateTime.Now as in all other sub-tasks: the container runs with TZ=Europe/Berlin,
        // "Sunday from 6 PM" should be user local time, not UTC.
        var now = DateTime.Now;
        if (now.DayOfWeek != DayOfWeek.Sunday || now.Hour < 18) return;

        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.WeeklyReportEnabled) return;

        // ISO week instead of calendar date as the dedup unit, so the year boundary
        // (week 52/53 <-> week 1) doesn't produce a duplicated or skipped report.
        var weekId = $"{System.Globalization.ISOWeek.GetYear(now)}-W{System.Globalization.ISOWeek.GetWeekOfYear(now):D2}";
        if (_weeklyReportSentForWeek.GetValueOrDefault(_currentAuthUserId) == weekId) return;

        var key = $"weeklyreport:{weekId}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key))
        {
            // After a restart on the same Sunday evening: the DB key wins, the memo just catches up.
            _weeklyReportSentForWeek[_currentAuthUserId] = weekId;
            return;
        }

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now))
        {
            _weeklyReportSentForWeek[_currentAuthUserId] = weekId;
            return;
        }

        // "Studied" = same semantics as dashboard and achievement check. Full scan as there;
        // all StartTimes are needed for the streak backward pass, not just two weeks.
        var studied = await db.Sessions
            .Where(s => s.IsCompleted || s.EndTime <= now)
            .Select(s => new { s.StartTime, s.EndTime, s.CourseName })
            .ToListAsync();

        // Week-start convention shared with the dashboard (StudyMetrics.WeekStartOf, Monday = 0).
        var today = now.Date;
        var weekStart = StudyMetrics.WeekStartOf(today);
        var lastWeekStart = weekStart.AddDays(-7);

        var thisWeek = studied.Where(s => s.StartTime.Date >= weekStart).ToList();
        var thisWeekHours = thisWeek.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var lastWeekHours = studied
            .Where(s => s.StartTime.Date >= lastWeekStart && s.StartTime.Date < weekStart)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        string body;
        if (!thisWeek.Any())
        {
            // Deliberately don't hide an empty week, but give a gentle nudge instead.
            body = "Diese Woche 0h gelernt - nächste Woche wird besser! 💪";
        }
        else
        {
            var streak = StudyMetrics.CalcStreak(studied.Select(s => s.StartTime), today);
            var topCourse = thisWeek
                .GroupBy(s => s.CourseName)
                .OrderByDescending(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours))
                .First().Key;
            var delta = thisWeekHours - lastWeekHours;
            body = $"{thisWeekHours:0.#}h gelernt ({(delta >= 0 ? "+" : "-")}{Math.Abs(delta):0.#}h vs. Vorwoche) · Streak: {streak} Tage · Top-Kurs: {topCourse}";
        }
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title = "Dein Wochenrückblick 📊", body });

        _logger.LogInformation("Sende Wochenrückblick '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Weekly recap push failed for {Endpoint}")));

        foreach (var result in results)
        {
            if (!result.Expired) continue;
            db.PushSubscriptions.Remove(result.Subscription);
            dbChanged = true;
        }

        if (dbChanged)
            await db.SaveChangesAsync();

        // Only memoize after a successful claim/save, so a failure gets retried on the next tick.
        _weeklyReportSentForWeek[_currentAuthUserId] = weekId;
    }
}
