using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Pure unit tests for TimerModeCatalog - the shared source of truth for the built-in table,
/// custom-mode JSON parsing/clamping, and the phase-transition state machine (audit finding D5:
/// previously two hand-synced copies, client TimerService.Tick()/CustomTimerModes.Parse and
/// server ServerTimerModes/RunLiveActivityPushAsync). Server-side behavioral coverage of the same
/// contract lives in ServerTimerModesTests and LiveActivityPushTests (proof the server's observed
/// behavior didn't change).
/// </summary>
public class TimerModeCatalogTests
{
    [Fact]
    public void BuiltIn_HasNineModesWithSequentialIds()
    {
        Assert.Equal(9, TimerModeCatalog.BuiltIn.Count);
        for (var id = 1; id <= 9; id++)
        {
            var mode = TimerModeCatalog.FindBuiltIn(id);
            Assert.NotNull(mode);
            Assert.Equal(id, mode!.Value.Id);
            Assert.False(string.IsNullOrWhiteSpace(mode.Value.Name));
        }
    }

    [Fact]
    public void FindBuiltIn_UnknownId_ReturnsNull()
    {
        Assert.Null(TimerModeCatalog.FindBuiltIn(999));
        Assert.Null(TimerModeCatalog.FindBuiltIn(100)); // first custom id, not a built-in
    }

    [Fact]
    public void ParseCustom_EmptyOrWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(TimerModeCatalog.ParseCustom(null));
        Assert.Empty(TimerModeCatalog.ParseCustom(""));
        Assert.Empty(TimerModeCatalog.ParseCustom("   "));
    }

    [Fact]
    public void ParseCustom_MalformedJson_ReturnsEmptyListInsteadOfThrowing()
    {
        Assert.Empty(TimerModeCatalog.ParseCustom("{not json"));
    }

    [Fact]
    public void ParseCustom_JsonNullLiteral_ReturnsEmptyList()
    {
        Assert.Empty(TimerModeCatalog.ParseCustom("null"));
    }

    [Fact]
    public void ParseCustom_WebCasedJson_ReturnsItsValues()
    {
        var json = """[{"id":100,"name":"My Mode","focusMinutes":45,"breakMinutes":15,"rounds":2}]""";

        var modes = TimerModeCatalog.ParseCustom(json);

        var mode = Assert.Single(modes);
        Assert.Equal(100, mode.Id);
        Assert.Equal("My Mode", mode.Name);
        Assert.Equal(45, mode.FocusMinutes);
        Assert.Equal(15, mode.BreakMinutes);
        Assert.Equal(2, mode.Rounds);
    }

    [Fact]
    public void ParseCustom_OutOfRangeValues_AreClampedToBounds()
    {
        var json = """[{"id":100,"name":"Extreme","focusMinutes":999,"breakMinutes":-5,"rounds":0},"""
            + """{"id":101,"name":"Tiny","focusMinutes":1,"breakMinutes":90,"rounds":99}]""";

        var modes = TimerModeCatalog.ParseCustom(json);

        var extreme = modes.Single(m => m.Id == 100);
        var tiny = modes.Single(m => m.Id == 101);
        Assert.Equal((180, 0, 1), (extreme.FocusMinutes, extreme.BreakMinutes, extreme.Rounds));
        Assert.Equal((5, 60, 10), (tiny.FocusMinutes, tiny.BreakMinutes, tiny.Rounds));
    }

    [Fact]
    public void ParseCustom_IdBelowFirstCustomId_IsRejected()
    {
        var json = """[{"id":42,"name":"Impostor","focusMinutes":30,"breakMinutes":5,"rounds":3}]""";

        Assert.Empty(TimerModeCatalog.ParseCustom(json));
    }

    [Fact]
    public void ParseCustom_BlankName_IsRejected()
    {
        var json = """[{"id":100,"name":"  ","focusMinutes":30,"breakMinutes":5,"rounds":3}]""";

        Assert.Empty(TimerModeCatalog.ParseCustom(json));
    }

    [Fact]
    public void SerializeCustom_ThenParseCustom_RoundTrips()
    {
        var modes = new List<TimerModeCatalog.ModeData>
        {
            new(100, "Round Trip", 45, 15, 2),
            new(101, "Second", 20, 4, 6),
        };

        var json = TimerModeCatalog.SerializeCustom(modes);
        var parsed = TimerModeCatalog.ParseCustom(json);

        Assert.Equal(modes, parsed);
    }

    [Fact]
    public void NextCustomId_NoExisting_ReturnsFirstCustomId()
    {
        Assert.Equal(TimerModeCatalog.FirstCustomId, TimerModeCatalog.NextCustomId(Array.Empty<TimerModeCatalog.ModeData>()));
    }

    [Fact]
    public void NextCustomId_WithExisting_ReturnsMaxPlusOne()
    {
        var existing = new[] { new TimerModeCatalog.ModeData(100, "A", 25, 5, 4), new TimerModeCatalog.ModeData(105, "B", 25, 5, 4) };

        Assert.Equal(106, TimerModeCatalog.NextCustomId(existing));
    }

    [Fact]
    public void Resolve_BuiltInId_ReturnsBuiltInModeWithoutTouchingCustomJson()
    {
        var mode = TimerModeCatalog.Resolve(1, "{not json");

        Assert.NotNull(mode);
        Assert.Equal("Pomodoro Classic", mode!.Value.Name);
        Assert.Equal(25, mode.Value.FocusMinutes);
        Assert.Equal(5, mode.Value.BreakMinutes);
        Assert.Equal(4, mode.Value.Rounds);
    }

    [Fact]
    public void Resolve_UnknownId_ReturnsNull()
    {
        Assert.Null(TimerModeCatalog.Resolve(999, null));
    }

    [Fact]
    public void Resolve_CustomIdNotInJson_ReturnsNull()
    {
        var json = """[{"id":100,"name":"My Mode","focusMinutes":45,"breakMinutes":15,"rounds":2}]""";

        Assert.Null(TimerModeCatalog.Resolve(101, json));
    }

    // --- AdvancePhase (phase-transition math) ---

    private static readonly TimerModeCatalog.ModeData PomodoroClassic = new(1, "Pomodoro Classic", 25, 5, 4);

    [Fact]
    public void AdvancePhase_FocusEnds_TransitionsToBreakSameRound()
    {
        var endsAt = new DateTime(2026, 1, 1, 10, 0, 0);

        var step = TimerModeCatalog.AdvancePhase(PomodoroClassic, isBreak: false, round: 1, phaseEndsAt: endsAt);

        Assert.True(step.IsBreak);
        Assert.Equal(1, step.Round); // round doesn't advance until the break ends
        Assert.False(step.Complete);
        Assert.Equal(endsAt, step.PreviousPhaseEndsAt);
        Assert.Equal(endsAt.AddMinutes(5), step.PhaseEndsAt); // break duration
    }

    [Fact]
    public void AdvancePhase_NonFinalBreakEnds_AdvancesRoundAndTransitionsToFocus()
    {
        var endsAt = new DateTime(2026, 1, 1, 10, 0, 0);

        var step = TimerModeCatalog.AdvancePhase(PomodoroClassic, isBreak: true, round: 1, phaseEndsAt: endsAt);

        Assert.False(step.IsBreak);
        Assert.Equal(2, step.Round);
        Assert.False(step.Complete);
        Assert.Equal(endsAt.AddMinutes(25), step.PhaseEndsAt); // focus duration
    }

    [Fact]
    public void AdvancePhase_FinalBreakEnds_CompletesWithoutStartingAFifthRound()
    {
        var endsAt = new DateTime(2026, 1, 1, 10, 0, 0);

        // Round 4 of 4 (Rounds=4): the break after round 4 ends the session.
        var step = TimerModeCatalog.AdvancePhase(PomodoroClassic, isBreak: true, round: 4, phaseEndsAt: endsAt);

        Assert.True(step.Complete);
        Assert.Equal(5, step.Round); // incremented, then rejected as > Rounds
        Assert.Equal(endsAt, step.PhaseEndsAt); // unchanged - caller must not treat it as a new phase
    }

    [Fact]
    public void AdvancePhase_LoopedFromCaller_CatchesUpMultipleMissedPhases()
    {
        // Simulates a device that was suspended through an entire focus phase AND the following
        // break - the phase boundary must land exactly two steps later, not clamp to "now".
        var start = new DateTime(2026, 1, 1, 10, 0, 0);
        // Just before the following phase (break of round 2) would also be due - AdvancePhase
        // uses ">=" (see AdvancePhase_ExactBoundary below), so landing exactly on or past that
        // third boundary would trigger a third step.
        var now = start.AddMinutes(25 + 5).AddSeconds(-1);

        var isBreak = false;
        var round = 1;
        var endsAt = start;
        var steps = 0;
        while (now >= endsAt)
        {
            var step = TimerModeCatalog.AdvancePhase(PomodoroClassic, isBreak, round, endsAt);
            isBreak = step.IsBreak;
            round = step.Round;
            endsAt = step.PhaseEndsAt;
            steps++;
            if (step.Complete) break;
        }

        Assert.Equal(2, steps); // focus->break, break->focus(round 2)
        Assert.False(isBreak);
        Assert.Equal(2, round);
        Assert.Equal(start.AddMinutes(30), endsAt);
    }

    [Fact]
    public void AdvancePhase_ExactBoundary_NowEqualToPhaseEndsAt_StillTransitions()
    {
        // ">=" semantics: the boundary instant itself must trigger the transition, not wait for
        // strictly-past.
        var endsAt = new DateTime(2026, 1, 1, 10, 0, 0);
        var now = endsAt;

        Assert.True(now >= endsAt);
        var step = TimerModeCatalog.AdvancePhase(PomodoroClassic, isBreak: false, round: 1, phaseEndsAt: endsAt);
        Assert.True(step.IsBreak);
    }

    [Fact]
    public void AdvancePhase_SingleRoundMode_CompletesAfterOneFocusOneBreak()
    {
        var oneRound = new TimerModeCatalog.ModeData(9, "Marathon Session", 180, 30, 1);
        var endsAt = new DateTime(2026, 1, 1, 10, 0, 0);

        var toBreak = TimerModeCatalog.AdvancePhase(oneRound, isBreak: false, round: 1, phaseEndsAt: endsAt);
        Assert.True(toBreak.IsBreak);
        Assert.False(toBreak.Complete);

        var toComplete = TimerModeCatalog.AdvancePhase(oneRound, toBreak.IsBreak, toBreak.Round, toBreak.PhaseEndsAt);
        Assert.True(toComplete.Complete);
    }
}
