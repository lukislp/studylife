using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/timerstate")]
public class TimerStateController : ControllerBase
{
    private readonly ITimerStateService _timerState;

    public TimerStateController(ITimerStateService timerState) => _timerState = timerState;

    [HttpGet]
    public async Task<TimerStateDto> Get() => await _timerState.GetAsync();

    /// <summary>
    /// Best effort, last-write-wins: a stale push is silently dropped rather than rejected, and a
    /// conflicting concurrent write is re-applied on top of the fresh row instead of answering
    /// 409 - see TimerStateService.SaveAsync for both, and why 409 would be useless on this
    /// fire-and-forget path.
    /// </summary>
    [HttpPut]
    public async Task<TimerStateDto> Save(TimerStateDto dto) => await _timerState.SaveAsync(dto);

    /// <summary>Deliberately a SEPARATE endpoint instead of a field on TimerStateDto/Save(): the
    /// normal state push from TimerService (start/pause/stop, runs on every platform incl. web)
    /// doesn't know about this app-only field and would otherwise overwrite it with null on
    /// every call. Only invoked by the app with the push entitlement (paid profile).</summary>
    [HttpPut("liveactivity-token")]
    public async Task<IActionResult> SetLiveActivityPushToken(LiveActivityPushTokenDto dto)
    {
        await _timerState.SetLiveActivityPushTokenAsync(dto);
        return Ok();
    }
}
