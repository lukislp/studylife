using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/timerstate")]
public class TimerStateController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public TimerStateController(StudyLifeDb db) => _db = db;

    [HttpGet]
    public async Task<TimerStateDto> Get()
    {
        var entity = await _db.TimerState.FirstOrDefaultAsync()
            ?? new TimerStateEntity();
        var dto = ToDto(entity);
        // Server clock as the shared reference for the remaining-time display on other
        // devices (see TimerStateDto.ServerNow) - DateTime.Now like everywhere else (TZ=Europe/Berlin).
        dto.ServerNow = DateTime.Now;
        return dto;
    }

    [HttpPut]
    public async Task<TimerStateDto> Save(TimerStateDto dto)
    {
        var entity = await _db.TimerState.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new TimerStateEntity();
            _db.TimerState.Add(entity);
        }
        entity.SessionId = dto.SessionId;
        entity.IsRunning = dto.IsRunning;
        entity.IsBreak = dto.IsBreak;
        entity.CurrentRound = dto.CurrentRound;
        entity.TimerModeId = dto.TimerModeId;
        entity.PhaseEndsAt = dto.PhaseEndsAt;
        entity.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    private static TimerStateDto ToDto(TimerStateEntity e) => new()
    {
        SessionId = e.SessionId,
        IsRunning = e.IsRunning,
        IsBreak = e.IsBreak,
        CurrentRound = e.CurrentRound,
        TimerModeId = e.TimerModeId,
        PhaseEndsAt = e.PhaseEndsAt,
        UpdatedAt = e.UpdatedAt,
    };

    /// <summary>Deliberately a SEPARATE endpoint instead of a field on TimerStateDto/Save(): the
    /// normal state push from TimerService (start/pause/stop, runs on every platform incl. web)
    /// doesn't know about this app-only field and would otherwise overwrite it with null on
    /// every call. Only invoked by the app with the push entitlement (paid profile).</summary>
    [HttpPut("liveactivity-token")]
    public async Task<IActionResult> SetLiveActivityPushToken(LiveActivityPushTokenDto dto)
    {
        var entity = await _db.TimerState.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new TimerStateEntity();
            _db.TimerState.Add(entity);
        }
        entity.LiveActivityPushToken = dto.Token;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
