using System.Text.Json;

namespace StudyLife.Shared;

/// <summary>
/// Single source of truth for timer modes (built-in table, custom-mode JSON parsing/clamping)
/// and the phase-transition state machine - previously two separately hand-synced copies
/// (audit finding D5, the last "keep in sync manually" family after AchievementCatalog.cs was
/// centralized for D1, see that file for the established pattern): StudyLife.Client
/// (TimerMode/CustomTimerModes in Models.cs, TimerService.Tick()) and StudyLife.Server
/// (ServerTimerModes, BackgroundTaskService.RunLiveActivityPushAsync - the Live Activity push
/// worker, which independently recomputes phase state while the device is locked/suspended and
/// can't tick itself). Only the numeric core lives here; the client's TimerMode still owns
/// presentation (Description/Style/Emoji/Gradient) and JS interop, the server still owns push
/// composition. No LINQ over value tuples in here (MAUI/iOS AOT: has crashed at compile time
/// before in this codebase) - plain loops and record structs only.
/// </summary>
public static class TimerModeCatalog
{
    /// <summary>Custom mode ids start here - never collides with the built-in range (1-9).</summary>
    public const int FirstCustomId = 100;

    public const int MinFocusMinutes = 5;
    public const int MaxFocusMinutes = 180;
    public const int MinBreakMinutes = 0;
    public const int MaxBreakMinutes = 60;
    public const int MinRounds = 1;
    public const int MaxRounds = 10;

    /// <summary>Numeric core shared by every timer mode (built-in or custom) - the subset the
    /// phase-transition math and the server's push notifier need. Client's TimerMode adds
    /// presentation fields (Description/Style/Emoji/Gradient) on top of this.</summary>
    public readonly record struct ModeData(int Id, string Name, int FocusMinutes, int BreakMinutes, int Rounds);

    /// <summary>Built-in mode table (ids 1-9) - the numeric core of DefaultData.TimerModes on the
    /// client and, previously, ServerTimerModes.BuiltIn on the server.</summary>
    public static readonly IReadOnlyList<ModeData> BuiltIn = new ModeData[]
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

    public static ModeData? FindBuiltIn(int id)
    {
        foreach (var m in BuiltIn)
            if (m.Id == id) return m;
        return null;
    }

    /// <summary>Compact JSON storage format: only the 5 user-defined fields, camelCase (matches
    /// UserSettings.CustomTimerModes, written by the client, read by both sides).</summary>
    private sealed class JsonEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int FocusMinutes { get; set; }
        public int BreakMinutes { get; set; }
        public int Rounds { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Tolerant parse of UserSettings.CustomTimerModes: empty/invalid JSON results in an
    /// empty list (fallback style used throughout the codebase). Clamps Focus/Break/Rounds to the
    /// same bounds as the built-ins and rejects ids below FirstCustomId or blank names, so a
    /// corrupted or hand-edited settings value can't produce a degenerate phase loop (e.g. a
    /// 0-minute break) or shadow a built-in id.</summary>
    public static List<ModeData> ParseCustom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<ModeData>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<JsonEntry>>(json, JsonOptions) ?? new();
            var result = new List<ModeData>();
            foreach (var e in entries)
            {
                if (e.Id < FirstCustomId || string.IsNullOrWhiteSpace(e.Name)) continue;
                result.Add(new ModeData(
                    e.Id,
                    e.Name,
                    Math.Clamp(e.FocusMinutes, MinFocusMinutes, MaxFocusMinutes),
                    Math.Clamp(e.BreakMinutes, MinBreakMinutes, MaxBreakMinutes),
                    Math.Clamp(e.Rounds, MinRounds, MaxRounds)));
            }
            return result;
        }
        catch (JsonException)
        {
            return new List<ModeData>();
        }
    }

    /// <summary>Serializes back to the compact storage format (client-only: the server never
    /// writes CustomTimerModes).</summary>
    public static string SerializeCustom(IEnumerable<ModeData> modes)
    {
        var entries = new List<JsonEntry>();
        foreach (var m in modes)
            entries.Add(new JsonEntry { Id = m.Id, Name = m.Name, FocusMinutes = m.FocusMinutes, BreakMinutes = m.BreakMinutes, Rounds = m.Rounds });
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    /// <summary>Next free custom id: max(existing custom ids, FirstCustomId - 1) + 1 -
    /// collision-free against the built-ins (client-only: ids are only ever minted there).</summary>
    public static int NextCustomId(IEnumerable<ModeData> existing)
    {
        var max = FirstCustomId - 1;
        foreach (var m in existing)
            if (m.Id > max) max = m.Id;
        return max + 1;
    }

    /// <summary>Built-in or custom mode by id - the numeric-only counterpart of
    /// CustomTimerModes.Combined on the client (which adds presentation fields). Used by the
    /// server's Live Activity push worker, which only ever needs the numeric fields.</summary>
    public static ModeData? Resolve(int id, string? customTimerModesJson)
    {
        var builtIn = FindBuiltIn(id);
        if (builtIn != null) return builtIn;

        if (id < FirstCustomId) return null;
        foreach (var m in ParseCustom(customTimerModesJson))
            if (m.Id == id) return m;
        return null;
    }

    /// <summary>Result of one phase-transition step: focus ends -> break begins (or, if the round
    /// count is exceeded, the session completes), break ends -> next round's focus begins.
    /// PreviousPhaseEndsAt is the boundary timestamp of the just-finished phase (the PhaseEndsAt
    /// going in) - client-only bookkeeping (the continuous-focus streak, OnBreakStarted) anchors
    /// to it instead of to "now", so a suspended-then-resumed device still accounts for exact
    /// elapsed time across the gap.</summary>
    public readonly record struct PhaseStep(bool IsBreak, int Round, DateTime PhaseEndsAt, bool Complete, DateTime PreviousPhaseEndsAt);

    /// <summary>One phase-transition step, pure (no I/O, no wall-clock reads, no side effects).
    /// Callers loop this while `now >= phaseEndsAt` to catch up on any number of missed phases
    /// (e.g. after a suspended device wakes up) - each subsequent phase starts at the END of the
    /// previous one (PhaseEndsAt going in), not "now", so total durations stay exact across the
    /// gap. Shared byte-for-byte by TimerService.Tick() (client) and RunLiveActivityPushAsync
    /// (server) - the two are guaranteed to compute identical phase transitions from identical
    /// inputs; any client-only side effects (streak accounting, events) are the caller's job.</summary>
    public static PhaseStep AdvancePhase(ModeData mode, bool isBreak, int round, DateTime phaseEndsAt)
    {
        var previous = phaseEndsAt;
        if (isBreak)
        {
            round++;
            if (round > mode.Rounds) return new PhaseStep(isBreak, round, phaseEndsAt, true, previous);
            isBreak = false;
            phaseEndsAt = phaseEndsAt.AddSeconds(mode.FocusMinutes * 60);
        }
        else
        {
            isBreak = true;
            phaseEndsAt = phaseEndsAt.AddSeconds(mode.BreakMinutes * 60);
        }
        return new PhaseStep(isBreak, round, phaseEndsAt, false, previous);
    }
}
