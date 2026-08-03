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
