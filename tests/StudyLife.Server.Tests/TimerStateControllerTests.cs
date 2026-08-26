using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Own class (= own factory/DB) for the default state on an untouched DB - the mutating PUT
/// tests in <see cref="TimerStateControllerTests"/> would otherwise overwrite it depending on
/// xUnit's execution order (singleton row like UserSettingsEntity).
/// </summary>
public class TimerStateControllerFreshDbTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TimerStateControllerFreshDbTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Get_OnFreshDatabase_ReturnsDefaultState()
    {
        var response = await _client.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<TimerStateDto>();

        Assert.NotNull(dto);
        Assert.Null(dto!.SessionId);
        Assert.False(dto.IsRunning);
        Assert.False(dto.IsBreak);
        Assert.Equal(0, dto.CurrentRound);
        Assert.Equal(0, dto.TimerModeId);
        Assert.Null(dto.PhaseEndsAt);
    }
}

public class TimerStateControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TimerStateControllerTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    private static TimerStateDto RunningFocusState() => new()
    {
        SessionId = 42,
        IsRunning = true,
        IsBreak = false,
        CurrentRound = 2,
        TimerModeId = 1,
        PhaseEndsAt = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Put_ThenGet_RoundTripsExactValues()
    {
        var state = RunningFocusState();

        var putResponse = await _client.PutAsJsonAsync("/api/timerstate", state);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putResult = await putResponse.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(state, putResult);
        // UpdatedAt is set server-side (DateTime.Now) - not part of the PUT input.
        Assert.True(putResult!.UpdatedAt >= DateTime.Now.AddMinutes(-2));

        var getResponse = await _client.GetAsync("/api/timerstate");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getResult = await getResponse.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(state, getResult);
    }

    [Fact]
    public async Task Put_Twice_UpdatesSingletonRowInsteadOfDuplicating()
    {
        var first = RunningFocusState();
        first.CurrentRound = 1;
        first.TimerModeId = 2;

        var second = RunningFocusState();
        second.SessionId = 77;
        second.IsRunning = false;
        second.IsBreak = true;
        second.CurrentRound = 4;
        second.TimerModeId = 3;
        second.PhaseEndsAt = null;

        var firstPut = await _client.PutAsJsonAsync("/api/timerstate", first);
        Assert.Equal(HttpStatusCode.OK, firstPut.StatusCode);

        var secondPut = await _client.PutAsJsonAsync("/api/timerstate", second);
        Assert.Equal(HttpStatusCode.OK, secondPut.StatusCode);

        var getResponse = await _client.GetAsync("/api/timerstate");
        var getResult = await getResponse.Content.ReadFromJsonAsync<TimerStateDto>();

        // Only the last PUT is visible - no "mixed" values from both calls, which would
        // indicate multiple rows instead of a single singleton row.
        AssertMatches(second, getResult);
    }

    /// <summary>
    /// Per the docs (TimerStateController), the endpoint is "best effort": no server-side
    /// plausibility check between the fields. A logically impossible/stale state
    /// (pause "running", but the phase is already in the past, no active session)
    /// must still be persisted without complaint.
    /// </summary>
    [Fact]
    public async Task Put_WithImplausibleStaleState_PersistsWithoutValidation()
    {
        var implausible = new TimerStateDto
        {
            SessionId = null,
            IsRunning = true,
            IsBreak = true,
            CurrentRound = -1,
            TimerModeId = 9999, // not a built-in or plausible custom mode
            PhaseEndsAt = new DateTime(2020, 1, 1), // far in the past
        };

        var putResponse = await _client.PutAsJsonAsync("/api/timerstate", implausible);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/timerstate");
        var getResult = await getResponse.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(implausible, getResult);
    }

    // ── ClientSequence semantics (audit S6) ─────────────────────────────────────
    // All four tests below share ONE singleton row with every other test in this class (see the
    // class doc comment) and xUnit does not guarantee method execution order - so each test's
    // "baseline" sequence is derived from the WALL CLOCK (FreshSequence) instead of a small
    // literal, guaranteeing it's always >= whatever any OTHER test in the class (running earlier
    // in real time, regardless of source order) could have stored, and "stale" is always
    // computed RELATIVE to that same test's own baseline. This makes every test self-contained
    // and order-independent, without needing per-test DB isolation.

    /// <summary>A fresh, always-increasing-over-real-time sequence value - see the class comment
    /// above for why every test computes its own instead of using small literals.</summary>
    private static long FreshSequence() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Put_WithStaleSequence_IsIgnored_ReturnsCurrentState()
    {
        var baseline = RunningFocusState();
        var baselineSeq = FreshSequence();
        baseline.ClientSequence = baselineSeq;
        var baselinePut = await _client.PutAsJsonAsync("/api/timerstate", baseline);
        Assert.Equal(HttpStatusCode.OK, baselinePut.StatusCode);

        // Simulates the out-of-order case (audit S6): TimerService fires PUTs unawaited, so an
        // OLDER transition's request can land on the server AFTER a newer one already did.
        var stale = RunningFocusState();
        stale.SessionId = 999;
        stale.CurrentRound = 99;
        stale.ClientSequence = baselineSeq - 1000; // clearly older than the baseline just written

        var staleResponse = await _client.PutAsJsonAsync("/api/timerstate", stale);
        Assert.Equal(HttpStatusCode.OK, staleResponse.StatusCode); // dropped silently, not 409 - see TimerStateController.Save
        var staleResult = await staleResponse.Content.ReadFromJsonAsync<TimerStateDto>();

        // The response reflects the CURRENT (baseline) row, not the stale payload that was sent.
        AssertMatches(baseline, staleResult);
        Assert.Equal(baselineSeq, staleResult!.ClientSequence);

        // A follow-up GET confirms the stale PUT never actually got persisted.
        var getResponse = await _client.GetAsync("/api/timerstate");
        var getResult = await getResponse.Content.ReadFromJsonAsync<TimerStateDto>();
        AssertMatches(baseline, getResult);
    }

    [Fact]
    public async Task Put_WithNewerSequence_IsApplied()
    {
        var baseline = RunningFocusState();
        var baselineSeq = FreshSequence();
        baseline.ClientSequence = baselineSeq;
        await _client.PutAsJsonAsync("/api/timerstate", baseline);

        var newer = RunningFocusState();
        newer.SessionId = 555;
        newer.CurrentRound = 3;
        newer.ClientSequence = baselineSeq + 1;

        var response = await _client.PutAsJsonAsync("/api/timerstate", newer);
        var result = await response.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(newer, result);
        Assert.Equal(baselineSeq + 1, result!.ClientSequence);
    }

    /// <summary>
    /// A pusher that doesn't know about sequence numbers at all (Home Assistant, or any client
    /// predating this field) sends no ClientSequence - that PUT must still overwrite
    /// unconditionally (plain last-write-wins, the exact behavior before ClientSequence existed),
    /// regardless of whatever sequence a sequence-aware pusher (TimerService) last recorded.
    /// </summary>
    [Fact]
    public async Task Put_WithoutSequence_AlwaysOverwrites_LastWriteWinsForHaCompat()
    {
        var sequenced = RunningFocusState();
        var seq = FreshSequence();
        sequenced.ClientSequence = seq;
        await _client.PutAsJsonAsync("/api/timerstate", sequenced);

        var unsequenced = RunningFocusState();
        unsequenced.SessionId = 777;
        unsequenced.IsRunning = false;
        unsequenced.ClientSequence = null;

        var response = await _client.PutAsJsonAsync("/api/timerstate", unsequenced);
        var result = await response.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(unsequenced, result);
        // The previously stored sequence is left untouched (not cleared) by the unsequenced
        // write - see the next test for why that matters.
        Assert.Equal(seq, result!.ClientSequence);
    }

    /// <summary>
    /// Companion to the previous test: an unsequenced write in between two sequenced ones must
    /// NOT reset the server's staleness tracking - otherwise a single HA/legacy write landing
    /// between two TimerService pushes would make the server accept an out-of-order TimerService
    /// push it should have rejected.
    /// </summary>
    [Fact]
    public async Task Put_WithSequence_AfterAnUnsequencedWrite_StillDetectsStaleness()
    {
        var sequenced = RunningFocusState();
        var seq = FreshSequence();
        sequenced.ClientSequence = seq;
        await _client.PutAsJsonAsync("/api/timerstate", sequenced);

        var unsequenced = RunningFocusState();
        unsequenced.SessionId = 111;
        unsequenced.ClientSequence = null;
        await _client.PutAsJsonAsync("/api/timerstate", unsequenced);

        var stale = RunningFocusState();
        stale.SessionId = 888;
        stale.ClientSequence = seq - 1000; // older than seq, even though the LAST accepted write had no sequence at all

        var response = await _client.PutAsJsonAsync("/api/timerstate", stale);
        var result = await response.Content.ReadFromJsonAsync<TimerStateDto>();

        AssertMatches(unsequenced, result); // still the unsequenced write's state, not the stale one
        Assert.Equal(seq, result!.ClientSequence);
    }

    private static void AssertMatches(TimerStateDto expected, TimerStateDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.SessionId, actual!.SessionId);
        Assert.Equal(expected.IsRunning, actual.IsRunning);
        Assert.Equal(expected.IsBreak, actual.IsBreak);
        Assert.Equal(expected.CurrentRound, actual.CurrentRound);
        Assert.Equal(expected.TimerModeId, actual.TimerModeId);
        Assert.Equal(expected.PhaseEndsAt, actual.PhaseEndsAt);
    }
}
