using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public partial class BackgroundTaskService
{
    // Curated quote pools per MotivationalStyle (Setup > SetupMotivationalStyleCard) - hardcoded
    // in German like all server push texts (cf. BackgroundTaskService.Reminders.cs); the i18n layer
    // (Toolbelt.Blazor.I18nText) is purely client-side and deliberately doesn't cover push payloads.
    private static readonly IReadOnlyDictionary<string, string[]> MotivationQuotes = new Dictionary<string, string[]>
    {
        ["claude"] = new[]
        {
            "Verstehen ist kein Zustand, sondern eine Praxis - heute ist ein guter Tag, sie zu üben.",
            "Nicht die Menge der Stunden zählt, sondern die Aufmerksamkeit in ihnen.",
            "Jedes schwierige Konzept war einmal unverständlich - auch für die, die es erfunden haben.",
            "Wer heute eine Frage mehr stellt, hat morgen eine Antwort mehr.",
            "Fortschritt fühlt sich selten wie Fortschritt an, während er passiert.",
            "Das Gelernte von gestern ist das Werkzeug von heute.",
            "Verwirrung ist kein Rückschritt - sie ist der Moment kurz vor dem Verstehen.",
            "Klein anfangen ist keine Schwäche. Nicht anfangen wäre eine.",
            "Wissen wächst nicht durch Wollen, sondern durch Wiederkehren.",
        },
        ["zen"] = new[]
        {
            "Ein Schritt. Dann der nächste. Mehr braucht es heute nicht.",
            "Atme ein, öffne das Buch, atme aus. Der Rest ergibt sich.",
            "Der Weg entsteht beim Gehen - auch beim Lernen.",
            "Heute musst du nicht alles verstehen. Nur ein wenig mehr als gestern.",
            "Ruhe ist keine Pause vom Lernen. Sie ist ein Teil davon.",
            "Wie Wasser den Stein formt, formt Wiederholung das Wissen.",
            "Lass den Vergleich mit anderen los. Dein Tempo ist dein Tempo.",
            "Ein ruhiger Geist lernt am tiefsten.",
            "Beginne dort, wo du bist. Das genügt.",
        },
        ["intense"] = new[]
        {
            "Keine Ausreden. Ein Kapitel. Jetzt.",
            "Disziplin schlägt Motivation - jeden einzelnen Tag.",
            "Während du zögerst, könnte die erste Session schon laufen.",
            "Müde? Egal. 25 Minuten gehen immer.",
            "Dein zukünftiges Ich fragt nicht, ob du Lust hattest - nur, ob du geliefert hast.",
            "Der Unterschied zwischen Bestehen und Bestleistung liegt in Tagen wie heute.",
            "Nicht morgen. Nicht später. Heute.",
            "Schwer ist gut. Schwer heißt, es zählt.",
            "Andere hoffen auf Ergebnisse. Du arbeitest dafür.",
        },
        ["hype"] = new[]
        {
            "LET'S GO! Heute wird gelernt wie noch nie! 🚀",
            "Du bist eine Lernmaschine - Zeit, das zu beweisen! 💪",
            "Neuer Tag, neues Level! Dein Streak wartet auf dich! 🔥",
            "Kaffee? Check. Motivation? Check. Dann mal los! ⚡",
            "Jede Session bringt dich näher ans Ziel - stack die Wins! 🏆",
            "Heute ist DER Tag für einen Riesen-Fortschritt! 🎯",
            "Dein Gehirn ist bereit für Großes - füttere es! 🧠",
            "Volle Energie voraus - dieser Tag gehört dir! 🌟",
            "Aufstehen, dranbleiben, abräumen - du schaffst das! 🙌",
        },
    };

    // From this hour onward (local time, container runs with TZ=Europe/Berlin) the daily push is due.
    // Deliberately fixed rather than tied to StudyWindowStartHour: that's a planner setting with
    // its own semantics ("from when may sessions be suggested"), not a push time.
    private const int DailyMotivationHour = 8;

    // internal + deterministic (date -> quote), so BackgroundTaskServiceTests can check the
    // selection directly; unknown styles fall back to "claude" (default in UserSettings).
    internal static string PickDailyMotivationQuote(string? style, DateTime date)
    {
        if (style == null || !MotivationQuotes.TryGetValue(style, out var pool))
            pool = MotivationQuotes["claude"];
        // Mix in the year so the same calendar day doesn't deliver the same quote every year.
        return pool[(date.Year + date.DayOfYear) % pool.Length];
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

        var body = PickDailyMotivationQuote(settings.MotivationalStyle, now.Date);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { title = "Motivation für heute ✨", body });

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
