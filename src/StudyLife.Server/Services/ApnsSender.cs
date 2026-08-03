using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyLife.Server.Services;

public enum ApnsSendOutcome
{
    Delivered,
    /// <summary>Token has been unregistered by Apple (app uninstalled or similar) - delete the
    /// subscription, same semantics as HTTP 410 for web push.</summary>
    ExpiredToken,
    Failed,
}

/// <summary>
/// APNs delivery channel for the native iOS app shell (studylife-app repo) - the counterpart
/// to the PWA's VAPID web push. This channel is the "power switch" for the paid upgrade: as
/// long as the Apns:* configuration is missing (KeyPath/KeyId/TeamId/BundleId), Enabled=false
/// and every send is a silent no-op - web push keeps running unaffected. Auth via p8 key
/// (JWT ES256, cached for ~45 min, Apple accepts 20-60 min); delivery via HTTP/2 to
/// api.push.apple.com or api.sandbox.push.apple.com (Apns:UseSandbox=true for
/// dev-signed builds - their tokens belong to the sandbox environment).
/// Apns:Endpoint overrides the target URL (test use only).
/// </summary>
public sealed class ApnsSender
{
    private const string ProductionEndpoint = "https://api.push.apple.com";
    private const string SandboxEndpoint = "https://api.sandbox.push.apple.com";
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(45);

    private readonly ILogger<ApnsSender> _logger;
    private readonly HttpClient _http;
    private readonly string? _keyPath;
    private readonly string? _keyId;
    private readonly string? _teamId;
    private readonly string? _bundleId;
    private readonly string _endpoint;

    private readonly object _jwtLock = new();
    private string? _cachedJwt;
    private DateTime _cachedJwtCreatedAt;

    public ApnsSender(IConfiguration configuration, ILogger<ApnsSender> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient();
        _keyPath = configuration["Apns:KeyPath"];
        _keyId = configuration["Apns:KeyId"];
        _teamId = configuration["Apns:TeamId"];
        _bundleId = configuration["Apns:BundleId"];
        _endpoint = configuration["Apns:Endpoint"]
            ?? (configuration.GetValue("Apns:UseSandbox", false) ? SandboxEndpoint : ProductionEndpoint);

        if (Enabled)
            _logger.LogInformation("APNs channel active (BundleId {BundleId}, Endpoint {Endpoint})", _bundleId, _endpoint);
        else if (new[] { _keyPath, _keyId, _teamId, _bundleId }.Any(v => !string.IsNullOrEmpty(v)))
            _logger.LogWarning("APNs configuration incomplete (KeyPath/KeyId/TeamId/BundleId must all be set) - channel stays off");
    }

    public bool Enabled =>
        !string.IsNullOrEmpty(_keyPath) && !string.IsNullOrEmpty(_keyId)
        && !string.IsNullOrEmpty(_teamId) && !string.IsNullOrEmpty(_bundleId);

    /// <summary>Takes the same payload JSON as the web push send ({"title":...,"body":...})
    /// and wraps it as an aps alert - both channels thereby share the entire payload
    /// construction in the worker.</summary>
    public async Task<ApnsSendOutcome> SendPayloadAsync(string deviceToken, string webPushPayloadJson)
    {
        if (!Enabled)
            return ApnsSendOutcome.Failed; // Callers treat Failed as a best-effort no-op

        string title = "StudyLife";
        string body = "";
        try
        {
            using var doc = JsonDocument.Parse(webPushPayloadJson);
            if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
            if (doc.RootElement.TryGetProperty("body", out var b)) body = b.GetString() ?? "";
        }
        catch (JsonException)
        {
            body = webPushPayloadJson; // defensive: show unknown payload raw instead of discarding it
        }

        var apsJson = JsonSerializer.Serialize(new
        {
            aps = new
            {
                alert = new { title, body },
                sound = "default",
            },
        });

        return await SendAsync(deviceToken, apsJson, "alert", priority: "10");
    }

    /// <summary>Live Activity update (step D): its own topic suffix + push-type "liveactivity",
    /// mandated by ActivityKit. content-state must match EXACTLY the field set/field names of
    /// TimerActivityAttributes.ContentState (studylife-app repo) - endsAt is deliberately Unix
    /// epoch seconds, because the app-side ContentState Codable implementation was explicitly
    /// switched to that (Swift's default Codable would otherwise expect seconds since the
    /// reference date 2001-01-01, not Unix epoch - a silent failure with no error message on our
    /// side at all, since Apple itself confirms the delivery as a success).</summary>
    public async Task<ApnsSendOutcome> SendLiveActivityUpdateAsync(string pushToken,
        DateTimeOffset endsAt, bool isBreak, int secondsLeft, int phaseTotalSeconds, int round, int totalRounds)
    {
        if (!Enabled) return ApnsSendOutcome.Failed;

        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["event"] = "update",
                ["content-state"] = BuildContentState(endsAt, isBreak, secondsLeft, phaseTotalSeconds, round, totalRounds),
            },
        };

        return await SendAsync(pushToken, payload.ToJsonString(), "liveactivity", priority: "10", topicSuffix: ".push-type.liveactivity");
    }

    /// <summary>Ends the Live Activity via push (session finished/cancelled while the device
    /// is locked) - content-state stays at the last known state, ActivityKit still needs it in
    /// the payload (dismissalDate = hide immediately).</summary>
    public async Task<ApnsSendOutcome> SendLiveActivityEndAsync(string pushToken,
        DateTimeOffset endsAt, bool isBreak, int secondsLeft, int phaseTotalSeconds, int round, int totalRounds)
    {
        if (!Enabled) return ApnsSendOutcome.Failed;

        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["event"] = "end",
                ["dismissal-date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["content-state"] = BuildContentState(endsAt, isBreak, secondsLeft, phaseTotalSeconds, round, totalRounds),
            },
        };

        return await SendAsync(pushToken, payload.ToJsonString(), "liveactivity", priority: "10", topicSuffix: ".push-type.liveactivity");
    }

    private static JsonObject BuildContentState(DateTimeOffset endsAt, bool isBreak, int secondsLeft,
        int phaseTotalSeconds, int round, int totalRounds) => new()
        {
            // Unix epoch seconds, not Swift's Codable default (seconds since 2001-01-01) - see
            // TimerActivityAttributes.ContentState (studylife-app repo), which deliberately has
            // its own Codable implementation instead of the synthesized one for this reason.
            ["endsAt"] = endsAt.ToUnixTimeSeconds(),
            ["isBreak"] = isBreak,
            ["isPaused"] = false,
            ["secondsLeft"] = secondsLeft,
            ["phaseTotalSeconds"] = phaseTotalSeconds,
            ["round"] = round,
            ["totalRounds"] = totalRounds,
        };

    private async Task<ApnsSendOutcome> SendAsync(string deviceToken, string apsJson, string pushType,
        string priority, string? topicSuffix = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/3/device/{deviceToken}")
            {
                Version = new Version(2, 0),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(apsJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("authorization", $"bearer {GetJwt()}");
            request.Headers.TryAddWithoutValidation("apns-topic", _bundleId + topicSuffix);
            request.Headers.TryAddWithoutValidation("apns-push-type", pushType);
            request.Headers.TryAddWithoutValidation("apns-priority", priority);

            using var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return ApnsSendOutcome.Delivered;

            var responseBody = await response.Content.ReadAsStringAsync();
            // 410 Gone = "Unregistered"; BadDeviceToken/DeviceTokenNotForTopic are equally
            // final (token does not (or no longer) belong to this app) - discard the subscription.
            if (response.StatusCode == System.Net.HttpStatusCode.Gone
                || responseBody.Contains("BadDeviceToken")
                || responseBody.Contains("DeviceTokenNotForTopic"))
                return ApnsSendOutcome.ExpiredToken;

            _logger.LogWarning("APNs send failed: HTTP {Status} {Body}", (int)response.StatusCode, responseBody);
            return ApnsSendOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "APNs send failed (network/protocol)");
            return ApnsSendOutcome.Failed;
        }
    }

    private string GetJwt()
    {
        lock (_jwtLock)
        {
            if (_cachedJwt != null && DateTime.UtcNow - _cachedJwtCreatedAt < JwtLifetime)
                return _cachedJwt;

            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(_keyPath!));

            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _keyId }));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = _teamId, iat = now }));
            var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");
            // SignData returns the IEEE P1363 format (r||s), which is exactly what JWS requires for ES256.
            var signature = Base64Url(key.SignData(signingInput, HashAlgorithmName.SHA256));

            _cachedJwt = $"{header}.{claims}.{signature}";
            _cachedJwtCreatedAt = DateTime.UtcNow;
            return _cachedJwt;
        }
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
