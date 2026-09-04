using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>PUT api/timerstate rebases PhaseEndsAt onto the server clock when the writer sends
/// ClientNow, so a device with a skewed clock cannot make the other devices' remote-timer banner
/// start above (or below) the phase length.</summary>
public class TimerStateClockTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TimerStateClockTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Put_WithSkewedClientClock_StoresTheDeadlineInServerTime()
    {
        var client = _factory.CreateClient();
        // Writer's clock is 4 s ahead of the server: it starts a 10-minute phase.
        var clientNow = DateTime.Now.AddSeconds(4);
        var put = await client.PutAsJsonAsync("/api/timerstate", new TimerStateDto
        {
            IsRunning = true,
            TimerModeId = 1,
            CurrentRound = 1,
            PhaseEndsAt = clientNow.AddMinutes(10),
            ClientNow = clientNow,
        });
        put.EnsureSuccessStatusCode();

        var got = await client.GetFromJsonAsync<TimerStateDto>("/api/timerstate");
        Assert.NotNull(got);
        Assert.NotNull(got!.PhaseEndsAt);
        Assert.NotNull(got.ServerNow);
        var remaining = (got.PhaseEndsAt!.Value - got.ServerNow!.Value).TotalSeconds;
        // 600 s minus the few hundred milliseconds between PUT and GET - never 604.
        Assert.InRange(remaining, 590, 600.5);
    }

    [Fact]
    public async Task Put_WithoutClientNow_KeepsTheDeadlineAsSent()
    {
        var client = _factory.CreateClient();
        var endsAt = DateTime.Now.AddMinutes(10).AddSeconds(4);
        var put = await client.PutAsJsonAsync("/api/timerstate", new TimerStateDto
        {
            IsRunning = true,
            TimerModeId = 1,
            CurrentRound = 1,
            PhaseEndsAt = endsAt,
        });
        put.EnsureSuccessStatusCode();
        var got = await client.GetFromJsonAsync<TimerStateDto>("/api/timerstate");
        Assert.Equal(endsAt, got!.PhaseEndsAt!.Value, TimeSpan.FromMilliseconds(5));
    }
}
