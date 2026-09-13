using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>Web-push subscription as a browser hands it over.</summary>
public record PushSubscribeRequest(string Endpoint, string P256dh, string Auth);

/// <summary>Registration of the native app shell (APNs channel, see ApnsSender). DeviceName
/// fills the UserAgent column for the device list (e.g. "Alex's iPhone").</summary>
public record ApnsSubscribeRequest(string Token, string? DeviceName);

/// <summary>
/// The /api/push domain operations: the VAPID public key, the web-push and APNs
/// subscribe/unsubscribe upserts, and the device list. <see cref="PushController"/> keeps route
/// binding, reading the User-Agent header off the request, and the status-code mapping - which
/// stays written out there rather than using the ServiceResult extensions, because these
/// endpoints answer <c>Ok()</c>/bare <c>BadRequest()</c> rather than the 200-with-body /
/// 400-with-message shape the extensions produce.
/// </summary>
public interface IPushService
{
    /// <summary>VAPID public key for Web Push registration in the browser.</summary>
    string PublicKey { get; }

    Task<ServiceResult> SubscribeAsync(PushSubscribeRequest dto, string? userAgent);

    /// <summary>Upserts the APNs registration. The token must already have passed
    /// <see cref="PushService.IsValidApnsToken"/> - that check is the controller's, because the
    /// endpoint answers it with a bodyless 400.</summary>
    Task SubscribeApnsAsync(ApnsSubscribeRequest dto);
    Task UnsubscribeAsync(string endpoint);
    Task UnsubscribeApnsAsync(string token);
    Task<List<PushSubscriptionListItemDto>> GetSubscriptionsAsync();
    Task<ServiceResult> DeleteSubscriptionAsync(int id);
}

public class PushService(StudyLifeDb db, VapidKeysHolder vapidKeysHolder) : IPushService
{
    /// <summary>Upper bound for the two base64url key strings a browser hands over with a
    /// subscription (p256dh is 65 bytes, auth 16 bytes - well under 200 chars encoded); anything
    /// larger is not a real PushSubscription and would only bloat the column.</summary>
    private const int MaxKeyLength = 512;

    private static readonly Regex ApnsTokenShape = new("^[A-Za-z0-9_-]{8,256}$", RegexOptions.Compiled);

    public string PublicKey => vapidKeysHolder.Keys!.PublicKey; // always set - see VapidKeysHolder comment

    public async Task<ServiceResult> SubscribeAsync(PushSubscribeRequest dto, string? userAgent)
    {
        // The endpoint is a URL the WORKER later POSTs to unattended - see OutboundUrlPolicy for
        // why it must be a public https origin and nothing else (2026-09 audit S4).
        if (!OutboundUrlPolicy.IsAcceptablePushEndpoint(dto.Endpoint))
            return ServiceResult.Invalid("Endpoint must be a public https URL.");
        if (string.IsNullOrWhiteSpace(dto.P256dh) || dto.P256dh.Length > MaxKeyLength
            || string.IsNullOrWhiteSpace(dto.Auth) || dto.Auth.Length > MaxKeyLength)
            return ServiceResult.Invalid("P256dh and Auth are required and must be at most 512 characters.");

        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == dto.Endpoint);

        if (existing == null)
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = dto.Endpoint,
                P256dh = dto.P256dh,
                Auth = dto.Auth,
                CreatedAt = DateTime.UtcNow,
                UserAgent = userAgent
            });
            await db.SaveChangesAsync();
        }
        else
        {
            existing.P256dh = dto.P256dh;
            existing.Auth = dto.Auth;
            // UserAgent is refreshed on every re-subscribe (browser update etc.),
            // but CreatedAt stays the original registration - that's the "registered X days ago".
            existing.UserAgent = userAgent;
            await db.SaveChangesAsync();
        }

        return ServiceResult.Success();
    }

    /// <summary>
    /// APNs device tokens are opaque hex in practice (Apple says not to assume the length);
    /// what matters here is that the value is interpolated into the APNs request PATH by
    /// ApnsSender, so anything outside a URL-path-safe alphabet - '/', '?', '#', '.' - is
    /// refused (2026-09 audit S13). Path-safe rather than strict hex keeps room for whatever
    /// Apple does next without reopening the injection surface. Static and called from the
    /// controller rather than folded into <see cref="SubscribeApnsAsync"/>, because that
    /// endpoint rejects with a bodyless 400 - there is no message for a result to carry.
    /// </summary>
    public static bool IsValidApnsToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && ApnsTokenShape.IsMatch(token);

    // APNs registration of the native app: same lifecycle as SubscribeAsync, just with a
    // device token instead of web-push credentials. The synthetic endpoint "apns:<token>"
    // serves the unique index, dedup, and EndpointHash of the device list unchanged (the
    // app computes its "this device" hash over the same synthetic value).
    public async Task SubscribeApnsAsync(ApnsSubscribeRequest dto)
    {
        var syntheticEndpoint = SyntheticApnsEndpoint(dto.Token);
        var deviceName = string.IsNullOrWhiteSpace(dto.DeviceName) ? "StudyLife App" : dto.DeviceName.Trim();

        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == syntheticEndpoint);

        if (existing == null)
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
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
        await db.SaveChangesAsync();
    }

    public Task UnsubscribeApnsAsync(string token) => UnsubscribeAsync(SyntheticApnsEndpoint(token));

    public async Task UnsubscribeAsync(string endpoint)
    {
        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (existing != null)
        {
            db.PushSubscriptions.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    // Device management (speed-dial FAB): Endpoint/P256dh/Auth are sensitive push credentials
    // and deliberately never leave the server here - EndpointHash is a one-way SHA256 that
    // the client computes identically for its own known subscription, to mark "this device"
    // without the real endpoint going out over the API.
    public async Task<List<PushSubscriptionListItemDto>> GetSubscriptionsAsync()
    {
        var subs = await db.PushSubscriptions.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return subs.Select(s => new PushSubscriptionListItemDto
        {
            Id = s.Id,
            CreatedAt = s.CreatedAt,
            UserAgent = s.UserAgent,
            EndpointHash = HashEndpoint(s.Endpoint)
        }).ToList();
    }

    public async Task<ServiceResult> DeleteSubscriptionAsync(int id)
    {
        var existing = await db.PushSubscriptions.FindAsync(id);
        if (existing == null) return ServiceResult.NotFound();

        db.PushSubscriptions.Remove(existing);
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    private static string SyntheticApnsEndpoint(string token) => $"apns:{token}";

    private static string HashEndpoint(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
}
