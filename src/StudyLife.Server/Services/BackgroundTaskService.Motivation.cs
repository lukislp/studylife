// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Texts;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // The quotes live in the server text catalogue (Texts/ServerText.<lang>.json) as
    // "motivation.<style>.<1..QuotesPerStyle>": one pool per MotivationalStyle (Setup >
    // SetupMotivationalStyleCard) - ServerTextTests keeps every style complete in every language.
    internal const int QuotesPerStyle = 9;

    // From this hour onward (local time, container runs with TZ=Europe/Berlin) the daily push is due.
    // Deliberately fixed rather than tied to StudyWindowStartHour: that's a planner setting with
    // its own semantics ("from when may sessions be suggested"), not a push time.
    private const int DailyMotivationHour = 8;

    // internal + deterministic (date -> quote), so BackgroundTaskServiceTests can check the
    // selection directly; unknown styles fall back to the default style (MotivationalStyles.Default); the retired
    // storage value is mapped there too, so rows written before the rename keep their pool.
    internal static string PickDailyMotivationQuote(string? style, DateTime date, string? language = null)
    {
        style = MotivationalStyles.ResolveOrDefault(style);
        // Mix in the year so the same calendar day doesn't deliver the same quote every year.
        var number = (date.Year + date.DayOfYear) % QuotesPerStyle + 1;
        return ServerText.Text($"motivation.{style}.{number}", language);
    }

    internal async Task RunDailyMotivationAsync(StudyLifeDb db, Func<Task<List<PushSubscriptionEntity>>> getSubscriptions)
    {
        // LocalNow (naive local wall clock) as in all other sub-tasks ("from 8 AM" is user local time, not UTC).
        var now = LocalNow;
        if (now.Hour < DailyMotivationHour) return;

        // Opt-in: without a settings row or with the toggle off, nothing gets computed further -
        // an inverted condition compared to the default-true toggles of the other sub-tasks.
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is not { DailyMotivationEnabled: true }) return;

        var dayId = $"{now:yyyyMMdd}";
        if (_dailyMotivationSentForDay.GetValueOrDefault(_currentAuthUserId) == dayId) return;

        var key = $"dailymotivation:{dayId}";
        if (await db.SentReminders.AnyAsync(r => r.Key == key))
        {
            // After a restart on the same day: the DB key wins, the memo just catches up.
            _dailyMotivationSentForDay[_currentAuthUserId] = dayId;
            return;
        }

        var subscriptions = await getSubscriptions();
        if (!subscriptions.Any()) return;

        if (!await TryClaimReminderAsync(db, key, now))
        {
            // Another worker committed the key first - just as with the Any check above,
            // the DB key wins, the memo just catches up, we don't send here.
            _dailyMotivationSentForDay[_currentAuthUserId] = dayId;
            return;
        }

        var body = PickDailyMotivationQuote(settings.MotivationalStyle, now.Date, settings.Language);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title = ServerText.Text("motivation.title", settings.Language), body });

        _logger.LogInformation("Sende Tages-Motivation '{Key}': {Body}", key, body);

        var client = GetPushClient();
        bool dbChanged = false;

        var results = await Task.WhenAll(subscriptions.Select(sub => SendPushAsync(client, sub, payload, "Motivation push failed for {Endpoint}")));

        foreach (var result in results)
        {
            if (!result.Expired) continue;
            db.PushSubscriptions.Remove(result.Subscription);
            dbChanged = true;
        }

        if (dbChanged)
            await db.SaveChangesAsync();

        // Only memoize after a successful claim/save, so a failure gets retried on the next tick.
        _dailyMotivationSentForDay[_currentAuthUserId] = dayId;
    }
}
