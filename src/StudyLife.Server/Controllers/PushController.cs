using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/push")]
public class PushController : ControllerBase
{
    private readonly IPushService _push;

    public PushController(IPushService push) => _push = push;

    [HttpGet("publickey")]
    public ActionResult<PushPublicKeyResponseDto> GetPublicKey() =>
        Ok(new PushPublicKeyResponseDto { PublicKey = _push.PublicKey });

    // The four subscribe/unsubscribe endpoints answer a bare Ok() (and, for subscribe-apns, a
    // bodyless BadRequest()) rather than the 200-with-body/400-with-message shape the
    // ServiceResult extensions produce - so their mapping is written out here instead, keeping
    // the responses byte-identical to what the clients have always received.

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(PushSubscribeRequest dto)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

        var result = await _push.SubscribeAsync(dto, userAgent);
        return result.Outcome == ServiceOutcome.Invalid ? BadRequest(result.Error) : Ok();
    }

    [HttpPost("subscribe-apns")]
    public async Task<IActionResult> SubscribeApns(ApnsSubscribeRequest dto)
    {
        if (!PushService.IsValidApnsToken(dto.Token)) return BadRequest();
        await _push.SubscribeApnsAsync(dto);
        return Ok();
    }

    [HttpPost("unsubscribe-apns")]
    public async Task<IActionResult> UnsubscribeApns(ApnsSubscribeRequest dto)
    {
        await _push.UnsubscribeApnsAsync(dto.Token);
        return Ok();
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(PushSubscribeRequest dto)
    {
        await _push.UnsubscribeAsync(dto.Endpoint);
        return Ok();
    }

    [HttpGet("subscriptions")]
    public async Task<IActionResult> GetSubscriptions() => Ok(await _push.GetSubscriptionsAsync());

    [HttpDelete("subscriptions/{id:int}")]
    public async Task<IActionResult> DeleteSubscription(int id) =>
        (await _push.DeleteSubscriptionAsync(id)).ToNoContentResult(this);
}
