using System.Security.Cryptography;
using System.Text;

namespace StudyLife.Server.Services;

/// <summary>
/// Mints short-lived signed tokens proving a proxied request to studylife-ai really comes
/// from an already-authenticated StudyLife session - NOT the user's AiApiKey (only a hash
/// of that is ever stored, see SettingsController's ai-api-key group; this backend cannot
/// retrieve the plaintext to forward it, discovered while first implementing the AI
/// integration - see studylife-ai's docs/decisions.md "M4.5 Multi-user support", "Auth flow,
/// take two"). studylife-ai verifies the token purely locally (no round-trip back here)
/// against the same shared secret (StudyLifeAi:SharedSecret) - the token format there
/// (Python) and here (C#) were verified byte-for-byte compatible before building on them.
///
/// Deliberately a static helper class instead of a DI service, same reasoning as
/// AuthSessionService: stateless, takes the caller's inputs directly.
/// </summary>
public static class AiProxyTokenService
{
    // Short - only needs to outlive the proxied request itself (including a slow LLM
    // response for /api/ai/agent), not a real session.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static string Mint(int userId, string sharedSecret, DateTime utcNow)
    {
        var expiry = new DateTimeOffset(utcNow + Lifetime).ToUnixTimeSeconds();
        var payload = $"{userId}.{expiry}";
        return $"{payload}.{Sign(payload, sharedSecret)}";
    }

    // HMAC-SHA256, base64url without padding - same encoding style as
    // AuthSessionService.GenerateToken/SettingsController.GenerateShareToken, just signed
    // instead of random.
    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
