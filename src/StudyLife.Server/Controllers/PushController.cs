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
    public ActionResult<PushPublicKeyResponseDto> GetPublicKey() =>
        Ok(new PushPublicKeyResponseDto { PublicKey = _vapidKeys.PublicKey });

    /// <summary>Upper bound for the two base64url key strings a browser hands over with a
    /// subscription (p256dh is 65 bytes, auth 16 bytes - well under 200 chars encoded); anything
    /// larger is not a real PushSubscription and would only bloat the column.</summary>
    private const int MaxKeyLength = 512;

    private static readonly System.Text.RegularExpressions.Regex ApnsTokenShape =
        new("^[A-Za-z0-9_-]{8,256}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(PushSubscribeRequest dto)
    {
        // The endpoint is a URL the WORKER later POSTs to unattended - see OutboundUrlPolicy for
        // why it must be a public https origin and nothing else (2026-09 audit S4).
        if (!OutboundUrlPolicy.IsAcceptablePushEndpoint(dto.Endpoint))
            return BadRequest("Endpoint must be a public https URL.");
        if (string.IsNullOrWhiteSpace(dto.P256dh) || dto.P256dh.Length > MaxKeyLength
            || string.IsNullOrWhiteSpace(dto.Auth) || dto.Auth.Length > MaxKeyLength)
            return BadRequest("P256dh and Auth are required and must be at most 512 characters.");

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
        // APNs device tokens are opaque hex in practice (Apple says not to assume the length);
        // what matters here is that the value is interpolated into the APNs request PATH by
        // ApnsSender, so anything outside a URL-path-safe alphabet - '/', '?', '#', '.' - is
        // refused (2026-09 audit S13). Path-safe rather than strict hex keeps room for whatever
        // Apple does next without reopening the injection surface.
        if (string.IsNullOrWhiteSpace(dto.Token) || !ApnsTokenShape.IsMatch(dto.Token)) return BadRequest();

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
