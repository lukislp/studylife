using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Second batch of new push categories: a gentle comeback nudge (counterpart to the notably
/// harsher InactivityRemindersEnabled) and a monthly report (counterpart to WeeklyReportEnabled,
/// see BackgroundTaskService.Reports.cs for the model). Instant feedback on a new record
/// (feature 2 of the same batch) deliberately does NOT live here but directly in SessionsController -
/// it reacts to a single request rather than the 30s polling cycle. The resource-link
/// extension (feature 3) is a pure text addition in RunPushNotificationsAsync
/// (BackgroundTaskService.Reminders.cs), likewise without its own method here.
/// </summary>
public partial class BackgroundTaskService
{
    /// <summary>
    /// Short, positively worded comeback nudge after EXACTLY 1 day of pause - deliberately much
    /// gentler/shorter than RunInactivityReminderCheckAsync, which only fires from
    /// InactivityThresholdDays (default 5 days) onward. From 2+ days of pause, the existing
    /// inactivity reminder takes over exclusively.
    /// Bug found live, twice: the original condition only checked "last past session was
    /// yesterday" - which a completely regular daily studier satisfies EVERY DAY until they log
    /// today's session, wrongly reading an ordinary in-progress day as "you paused yesterday".
    /// The message text ("Kleine Pause gestern") is about YESTERDAY having been empty, so that's
    /// what must actually be checked - not merely that yesterday was the most recent session.
    /// </summary>
    internal async Task RunComebackNudgeCheckAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { ComebackNudgeEnabled: true }) return;

        var now = LocalNow;
        // Gate on "late in the day" (same threshold as RunStreakRiskCheckAsync) so a same-day
        // session that simply hasn't happened yet isn't mistaken for "nothing planned today".
        var thresholdHour = Math.Clamp(settings.StudyWindowEndHour - 1, 18, 22);
        if (now.Hour < thresholdHour) return;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var dayBeforeYesterday = today.AddDays(-2);

        var studiedToday = await db.Sessions.AnyAsync(s => s.StartTime <= now && s.StartTime.Date == today);
        if (studiedToday) return;
        var studiedYesterday = await db.Sessions.AnyAsync(s => s.StartTime.Date == yesterday);
        if (studiedYesterday) return;
        // Only EXACTLY 1 day of pause - if the day before yesterday was empty too, that's 2+ days,
        // solely the job of the inactivity reminder.
        var studiedDayBeforeYesterday = await db.Sessions.AnyAsync(s => s.StartTime.Date == dayBeforeYesterday);
        if (!studiedDayBeforeYesterday) return;

        var key = $"comebacknudge:{today:yyyyMMdd}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key)) return;

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now)) return;

        var title = "Willkommen zurück 👋";
        var body = "Kleine Pause gestern - alles gut, mach weiter wo du warst!";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });

        _logger.LogInformation("Sende Comeback-Nudge '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Comeback nudge push failed for {Endpoint}")));
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
    /// Monthly report, analogous to RunWeeklyReportAsync (BackgroundTaskService.Reports.cs): fires
    /// on the 1st of the following month (from 9 AM local time), summarizing the PAST calendar month.
    /// The dedup unit is the calendar month ("yyyy-MM" of the summarized month), analogous to
    /// the ISO week for the weekly report.
    /// </summary>
    internal async Task RunMonthlyReportAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        var now = LocalNow;
        if (now.Day != 1 || now.Hour < 9) return;

        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings != null && !settings.MonthlyReportEnabled) return;

        var reportMonthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
        var monthId = $"{reportMonthStart:yyyy-MM}";
        if (_monthlyReportSentForMonth.GetValueOrDefault(_currentAuthUserId) == monthId) return;

        var key = $"monthlyreport:{monthId}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key))
        {
            // After a restart on the same day: the DB key wins, the memo just catches up.
            _monthlyReportSentForMonth[_currentAuthUserId] = monthId;
            return;
        }

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now))
        {
            _monthlyReportSentForMonth[_currentAuthUserId] = monthId;
            return;
        }

        var reportMonthEnd = reportMonthStart.AddMonths(1);
        var priorMonthStart = reportMonthStart.AddMonths(-1);

        // "Studied" = same semantics as weekly report/achievement check. Loading two months
        // is enough here (unlike the streak backward pass of the weekly report), because only
        // sums for the report month and the immediately preceding month are needed.
        var studied = await db.Sessions
            .Where(s => s.StartTime >= priorMonthStart && s.StartTime < reportMonthEnd)
            .Where(s => s.IsCompleted || s.EndTime <= now)
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();

        var reportMonth = studied.Where(s => s.StartTime >= reportMonthStart && s.StartTime < reportMonthEnd).ToList();
        var reportMonthHours = reportMonth.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var priorMonthHours = studied
            .Where(s => s.StartTime >= priorMonthStart && s.StartTime < reportMonthStart)
            .Sum(s => (s.EndTime - s.StartTime).TotalHours);

        var completedIds = (settings?.CompletedCourseIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => int.TryParse(x, out _))
            .Select(int.Parse)
            .ToList();

        // ECTS progress as a snapshot at report time (not limited to the report month) -
        // the same program-aware catalog selection as RunAchievementCheckAsync.
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

        var monthName = reportMonthStart.ToString("MMMM", new CultureInfo("de-DE"));

        string body;
        if (reportMonth.Count == 0)
        {
            body = $"Im {monthName} 0h gelernt - neuer Monat, neue Chance! 💪";
        }
        else
        {
            var delta = reportMonthHours - priorMonthHours;
            body = $"{reportMonthHours:0.#}h im {monthName} ({(delta >= 0 ? "+" : "-")}{Math.Abs(delta):0.#}h vs. Vormonat) · {reportMonth.Count} Sessions · {ectsEarned}/{ectsTotal} ECTS";
        }
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title = "Dein Monatsrückblick 🗓", body });

        _logger.LogInformation("Sende Monatsrückblick '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Monthly recap push failed for {Endpoint}")));
        foreach (var result in results)
        {
            if (!result.Expired) continue;
            db.PushSubscriptions.Remove(result.Subscription);
            dbChanged = true;
        }

        if (dbChanged)
            await db.SaveChangesAsync();

        // Only memoize after a successful claim/save, so a failure gets retried on the next tick.
        _monthlyReportSentForMonth[_currentAuthUserId] = monthId;
    }
}
