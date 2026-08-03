using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Four new, deliberately more assertive nudge categories (batch 1) - all default false/opt-in
/// like DailyMotivation/PerCourseInactivity (see BackgroundTaskService.Reminders.cs/.Motivation.cs),
/// since unlike the "classic" reminders above, they don't react to a concrete appointment
/// but proactively to behavior patterns (streak, weekly-goal pace, course progress, time of day).
/// </summary>
public partial class BackgroundTaskService
{
    private static List<int> ParseCourseIdList(string? commaSeparated) =>
        (commaSeparated ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();

    /// <summary>
    /// Warns late in the day if the current study streak would still break today. Uses
    /// StudyMetrics.CalcStreak (logic shared with Index.razor/Stats.razor/RunAchievementCheckAsync):
    /// without an entry for today, the calculation automatically anchors on yesterday - the result
    /// therefore already IS exactly the streak that breaks if nothing happens today.
    /// </summary>
    internal async Task RunStreakRiskCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { StreakRiskRemindersEnabled: true }) return;

        var now = DateTime.Now;
        // "Late in the day" relative to the configured study-window end instead of a fixed value:
        // one hour before StudyWindowEndHour, clamped to 6-10 PM - with the default (9 PM)
        // this yields exactly the "from 8 PM" mentioned in the feature spec.
        var thresholdHour = Math.Clamp(settings.StudyWindowEndHour - 1, 18, 22);
        if (now.Hour < thresholdHour) return;

        var today = now.Date;
        var studied = await db.Sessions
            .Where(s => s.IsCompleted || s.EndTime <= now)
            .Select(s => s.StartTime)
            .ToListAsync();

        // Already studied today -> streak not at risk, nothing to do.
        if (studied.Any(t => t.Date == today)) return;

        var streak = StudyMetrics.CalcStreak(studied, today);
        if (streak < 2) return;

        var key = $"streakrisk:{today:yyyyMMdd}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key)) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now)) return;

        var title = "Dein Streak reißt heute! 🔥";
        var body = $"Dein {streak}-Tage-Streak reißt heute, wenn du jetzt nicht noch eine Session machst.";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        _logger.LogInformation("Sende Streak-Risiko-Push '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Streak risk push failed for {Endpoint}")));
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
    /// Gentle mid-week nudge: checked from Thursday onward, fires if the hours studied so far
    /// are significantly (&lt;50%) below the proportional path toward WeeklyGoalMinHours (path = goal ×
    /// elapsed week fraction, the same proration idea as StudyMetrics.ProrateMonthlyTarget,
    /// deliberately kept local here rather than added there - this is a pure push heuristic, not a UI target value).
    /// </summary>
    internal async Task RunWeeklyGoalNudgeCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { WeeklyGoalNudgeEnabled: true }) return;

        var now = DateTime.Now;
        // ISO weekday (Monday=1..Sunday=7): "Thursday or later" means isoDayOfWeek >= 4.
        var isoDayOfWeek = ((int)now.DayOfWeek + 6) % 7 + 1;
        if (isoDayOfWeek < 4) return;

        var weekId = $"{System.Globalization.ISOWeek.GetYear(now)}-W{System.Globalization.ISOWeek.GetWeekOfYear(now):D2}";
        var key = $"weeklygoalnudge:{weekId}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key)) return;

        var today = now.Date;
        var weekStart = StudyMetrics.WeekStartOf(today);

        var thisWeek = await db.Sessions
            .Where(s => s.StartTime.Date >= weekStart && (s.IsCompleted || s.EndTime <= now))
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();
        var studiedHours = thisWeek.Sum(s => (s.EndTime - s.StartTime).TotalHours);

        // Elapsed week fraction (0..1) on an hour basis, capped at one full week.
        var elapsedFraction = Math.Min(1.0, (now - weekStart).TotalHours / (7 * 24));
        var expectedSoFar = settings.WeeklyGoalMinHours * elapsedFraction;
        if (expectedSoFar <= 0 || studiedHours >= expectedSoFar * 0.5) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now)) return;

        var title = "Wochenziel in Gefahr 📉";
        var body = $"Bisher {studiedHours:0.#}h diese Woche von angestrebten {settings.WeeklyGoalMinHours}h - noch ist Zeit, aufzuholen!";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        _logger.LogInformation("Sende Wochenziel-Nudge '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Weekly goal nudge push failed for {Endpoint}")));
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
    /// "Almost done" nudge per active course (selected, not completed): &gt;=85% but &lt;100%
    /// topic progress (CourseGoalEntity.CompletedTopics vs. CourseDto.Topics, the same formula
    /// as ProgressController.ComputeTopicProgressPercent, deliberately kept separate here rather
    /// than shared - there it's a private read projection for the share link, here it's a
    /// push decision with an additional inactivity criterion) AND no session in this course
    /// for >=7 days. Program-aware like RunAchievementCheckAsync (built-in catalog vs.
    /// custom study program).
    /// </summary>
    internal async Task RunCourseAlmostDoneCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { CourseAlmostDoneRemindersEnabled: true }) return;

        var selectedIds = ParseCourseIdList(settings.SelectedCourseIds);
        var completedIds = ParseCourseIdList(settings.CompletedCourseIds);
        var activeIds = new HashSet<int>(selectedIds);
        activeIds.ExceptWith(completedIds);
        if (activeIds.Count == 0) return;

        List<CourseDto> catalog = settings.ActiveStudyProgramId is int programId
            ? await StudyProgramCatalog.LoadCoursesAsync(db, programId)
            : CourseCatalog.AppliedAICourses;

        var activeCourses = catalog.Where(c => activeIds.Contains(c.Id) && c.Topics.Count > 0).ToList();
        if (activeCourses.Count == 0) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        var now = DateTime.Now;
        var today = now.Date;
        var weekId = $"{System.Globalization.ISOWeek.GetYear(now)}-W{System.Globalization.ISOWeek.GetWeekOfYear(now):D2}";

        var goalsByCourseId = await db.CourseGoals
            .Where(g => activeIds.Contains(g.CourseId))
            .ToDictionaryAsync(g => g.CourseId);

        var client = GetPushClient();
        bool dbChanged = false;

        foreach (var course in activeCourses)
        {
            var completedTopics = goalsByCourseId.TryGetValue(course.Id, out var goal) && !string.IsNullOrWhiteSpace(goal.CompletedTopics)
                ? goal.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()
                : new HashSet<string>();
            var doneCount = course.Topics.Count(t => completedTopics.Contains(t));
            var percent = doneCount * 100.0 / course.Topics.Count;
            if (percent < 85 || percent >= 100) continue;

            var lastSession = await db.Sessions
                .Where(s => s.CourseId == course.Id && s.StartTime <= now)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();
            // Never had a session: also counts as "nothing done in a long time".
            var daysSince = lastSession == null ? int.MaxValue : (today - lastSession.StartTime.Date).Days;
            if (daysSince < 7) continue;

            var key = $"coursealmostdone:{course.Id}:{weekId}";
            if (await db.SentReminders.AnyAsync(r => r.Key == key)) continue;
            if (!await TryClaimReminderAsync(db, key, now)) continue;

            var title = "Fast geschafft! 🎯";
            var body = $"{course.Name} steht bei {(int)Math.Round(percent)}% - nur noch ein kleiner Rest bis zum Ziel!";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

            _logger.LogInformation("Sende Fast-geschafft-Push '{Key}': {Body}", key, body);

            var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Almost-done push failed for {Endpoint}")));
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

    /// <summary>
    /// Reminds shortly before the historically most productive time of day (2h bucket with the
    /// highest summed session duration over the last 30 days), provided there's enough history
    /// overall (>=10 completed sessions) and nothing has been studied yet today. Runs (like the
    /// other checks here) only hourly - an exact "15 minutes before" therefore can't be guaranteed
    /// to be hit, so the time window is handled loosely (15 min before to 30 min after);
    /// the daily dedup key still prevents firing twice.
    /// </summary>
    internal async Task RunBestStudyTimeCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { BestStudyTimeRemindersEnabled: true }) return;

        var now = DateTime.Now;
        var today = now.Date;

        var totalStudiedCount = await db.Sessions.CountAsync(s => s.IsCompleted || s.EndTime <= now);
        if (totalStudiedCount < 10) return;

        var studiedToday = await db.Sessions.AnyAsync(s => s.StartTime.Date == today && (s.IsCompleted || s.EndTime <= now));
        if (studiedToday) return;

        var key = $"beststudytime:{today:yyyyMMdd}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key)) return;

        var recentCutoff = today.AddDays(-30);
        var recent = await db.Sessions
            .Where(s => s.StartTime >= recentCutoff && s.StartTime <= now && (s.IsCompleted || s.EndTime <= now))
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();
        if (recent.Count == 0) return;

        // 2-hour buckets (0-1, 2-3, ..., 22-23), "density" = summed session duration per bucket -
        // a single long session counts for more than several short fragments here.
        var bestBucket = recent
            .GroupBy(s => s.StartTime.Hour / 2)
            .Select(g => new { Bucket = g.Key, Hours = g.Sum(s => (s.EndTime - s.StartTime).TotalHours) })
            .OrderByDescending(x => x.Hours)
            .First();
        var bucketStartHour = bestBucket.Bucket * 2;

        var minutesUntilBucket = bucketStartHour * 60 - (now.Hour * 60 + now.Minute);
        var isNearBucketStart = minutesUntilBucket is <= 15 and >= -30;
        if (!isNearBucketStart) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now)) return;

        var title = "Beste Lernzeit steht an ⏰";
        var body = $"Zwischen {bucketStartHour:00}:00 und {bucketStartHour + 2:00}:00 Uhr bist du meistens am produktivsten - jetzt ist ein guter Moment für eine Session!";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        _logger.LogInformation("Sende Beste-Lernzeit-Push '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Best study time push failed for {Endpoint}")));
        foreach (var result in results)
        {
            if (!result.Expired) continue;
            db.PushSubscriptions.Remove(result.Subscription);
            dbChanged = true;
        }

        if (dbChanged)
            await db.SaveChangesAsync();
    }
}
