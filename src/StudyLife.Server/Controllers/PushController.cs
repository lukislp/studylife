using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public record PushSubscribeRequest(string Endpoint, string P256dh, string Auth);

/// <summary>Registration of the native app shell (APNs channel, see ApnsSender). DeviceName
/// fills the UserAgent column for the device list (e.g. "Alex's iPhone").</summary>
public record ApnsSubscribeRequest(string Token, string? DeviceName);

[ApiController]
[Route("api/push")]
public class PushController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly VapidKeys _vapidKeys;

    public PushController(StudyLifeDb db, VapidKeysHolder vapidKeysHolder)
    {
        _db = db;
        _vapidKeys = vapidKeysHolder.Keys!; // always set - see VapidKeysHolder comment
    }

    [HttpGet("publickey")]
    public IActionResult GetPublicKey() =>
        Ok(new { publicKey = _vapidKeys.PublicKey });

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(PushSubscribeRequest dto)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;

        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == dto.Endpoint);

        if (existing == null)
        {
            _db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = dto.Endpoint,
                P256dh = dto.P256dh,
                Auth = dto.Auth,
                CreatedAt = DateTime.UtcNow,
                UserAgent = userAgent
            });
            await _db.SaveChangesAsync();
        }
        else
        {
            existing.P256dh = dto.P256dh;
            existing.Auth = dto.Auth;
            // UserAgent is refreshed on every re-subscribe (browser update etc.),
            // but CreatedAt stays the original registration - that's the "registered X days ago".
            existing.UserAgent = userAgent;
            await _db.SaveChangesAsync();
        }

        return Ok();
    }

    // APNs registration of the native app: same lifecycle as subscribe, just with a
    // device token instead of web-push credentials. The synthetic endpoint "apns:<token>"
    // serves the unique index, dedup, and EndpointHash of the device list unchanged (the
    // app computes its "this device" hash over the same synthetic value).
    [HttpPost("subscribe-apns")]
    public async Task<IActionResult> SubscribeApns(ApnsSubscribeRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Token)) return BadRequest();

        var syntheticEndpoint = $"apns:{dto.Token}";
        var deviceName = string.IsNullOrWhiteSpace(dto.DeviceName) ? "StudyLife App" : dto.DeviceName.Trim();

        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == syntheticEndpoint);

        if (existing == null)
        {
            _db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = syntheticEndpoint,
                Channel = PushSubscriptionEntity.ChannelApns,
                ApnsToken = dto.Token,
                CreatedAt = DateTime.UtcNow,
                UserAgent = deviceName
            });
        }
        else
        {
            existing.UserAgent = deviceName; // like the web subscribe: refresh the display, keep CreatedAt
        }
        await _db.SaveChangesAsync();

        return Ok();
    }

    [HttpPost("unsubscribe-apns")]
    public async Task<IActionResult> UnsubscribeApns(ApnsSubscribeRequest dto)
    {
        var syntheticEndpoint = $"apns:{dto.Token}";
        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == syntheticEndpoint);
        if (existing != null)
        {
            _db.PushSubscriptions.Remove(existing);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(PushSubscribeRequest dto)
    {
        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == dto.Endpoint);
        if (existing != null)
        {
            _db.PushSubscriptions.Remove(existing);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // Device management (speed-dial FAB): Endpoint/P256dh/Auth are sensitive push credentials
    // and deliberately never leave the server here - EndpointHash is a one-way SHA256 that
    // the client computes identically for its own known subscription, to mark "this device"
    // without the real endpoint going out over the API.
    [HttpGet("subscriptions")]
    public async Task<IActionResult> GetSubscriptions()
    {
        var subs = await _db.PushSubscriptions.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var items = subs.Select(s => new PushSubscriptionListItemDto
        {
            Id = s.Id,
            CreatedAt = s.CreatedAt,
            UserAgent = s.UserAgent,
            EndpointHash = HashEndpoint(s.Endpoint)
        }).ToList();

        return Ok(items);
    }

    [HttpDelete("subscriptions/{id:int}")]
    public async Task<IActionResult> DeleteSubscription(int id)
    {
        var existing = await _db.PushSubscriptions.FindAsync(id);
        if (existing == null) return NotFound();

        _db.PushSubscriptions.Remove(existing);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string HashEndpoint(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
}
