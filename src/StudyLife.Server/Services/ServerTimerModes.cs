using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Server-side view of a timer mode: only the fields the Live Activity push worker needs
/// (Name for the push title, FocusMinutes/BreakMinutes/Rounds for the phase math). Thin adapter
/// over StudyLife.Shared.TimerModeCatalog, which owns the actual built-in table and custom-JSON
/// parsing/clamping (audit finding D5 - this used to be a hand-copied duplicate of the client's
/// CustomTimerModes.Parse, see AchievementCatalog.cs for the established centralization pattern).
/// Kept as a distinct type (rather than exposing TimerModeCatalog.ModeData directly) so the
/// server's call sites and existing tests don't need to change.
/// </summary>
public sealed record ServerTimerMode(int Id, string Name, int FocusMinutes, int BreakMinutes, int Rounds);

public static class ServerTimerModes
{
    /// <summary>Built-in + parsed custom modes, same order/ids as CustomTimerModes.Combined in
    /// the client - both ultimately read from TimerModeCatalog.</summary>
    public static ServerTimerMode? Resolve(int timerModeId, string? customTimerModesJson)
    {
        var mode = TimerModeCatalog.Resolve(timerModeId, customTimerModesJson);
        if (mode is not { } m) return null;
        return new ServerTimerMode(m.Id, m.Name, m.FocusMinutes, m.BreakMinutes, m.Rounds);
    }
}
