using System.Text.Json;

namespace StudyLife.Server.Services;

/// <summary>
/// Server-side counterpart to StudyLife.Client.Models.TimerMode/CustomTimerModes - deliberately
/// duplicated instead of a project restructuring (TimerMode lives in StudyLife.Client, which the
/// server doesn't reference): the worker (LiveActivityPushService) needs to know focus/break
/// minutes and round count to recompute phase transitions independently of the client (Live
/// Activity push, while the device is locked/suspended). ONLY the fields needed for the phase
/// math (Name for the push title, FocusMinutes/BreakMinutes/Rounds) - clamping rules and the
/// custom ID scheme (starting at 100) are copied 1:1 from CustomTimerModes.Parse and must be
/// kept in sync with any changes there.
/// </summary>
public sealed record ServerTimerMode(int Id, string Name, int FocusMinutes, int BreakMinutes, int Rounds);

public static class ServerTimerModes
{
    private const int FirstCustomId = 100;

    private static readonly IReadOnlyList<ServerTimerMode> BuiltIn = new List<ServerTimerMode>
    {
        new(1, "Pomodoro Classic", 25, 5, 4),
        new(2, "Flow State", 52, 17, 3),
        new(3, "Ultradian Rhythm", 90, 20, 2),
        new(4, "Claude Mode", 40, 10, 3),
        new(5, "Sprint Bursts", 10, 3, 6),
        new(6, "Micro Focus", 5, 1, 8),
        new(7, "Quick Burst", 15, 3, 5),
        new(8, "Deep Dive", 120, 20, 2),
        new(9, "Marathon Session", 180, 30, 1),
    };

    private sealed class Entry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int FocusMinutes { get; set; }
        public int BreakMinutes { get; set; }
        public int Rounds { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Built-in + parsed custom modes, same order/ids as
    /// CustomTimerModes.Combined in the client.</summary>
    public static ServerTimerMode? Resolve(int timerModeId, string? customTimerModesJson)
    {
        var builtIn = BuiltIn.FirstOrDefault(m => m.Id == timerModeId);
        if (builtIn != null) return builtIn;

        if (string.IsNullOrWhiteSpace(customTimerModesJson)) return null;
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(customTimerModesJson, JsonOptions) ?? new();
            var entry = entries.FirstOrDefault(e => e.Id == timerModeId && e.Id >= FirstCustomId
                && !string.IsNullOrWhiteSpace(e.Name));
            if (entry == null) return null;
            return new ServerTimerMode(
                entry.Id,
                entry.Name,
                Math.Clamp(entry.FocusMinutes, 5, 180),
                Math.Clamp(entry.BreakMinutes, 0, 60),
                Math.Clamp(entry.Rounds, 1, 10));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
