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
/// against the same signing secret(s) (StudyLifeAi:TokenSigningSecret, or the legacy
/// StudyLifeAi:SharedSecret while that's still configured - see audit finding A5 and
/// studylife-ai's docs/decisions.md "Split the shared secret (audit A5)") - the token format
/// there (Python) and here (C#) were verified byte-for-byte compatible before building on them.
///
/// Audit A5 split ONE symmetric shared secret (usable to both mint a token for ANY user_id
/// and administer /internal/*) into a signing secret with a key-id for rotation, and a
/// separate static internal-API bearer (see AiProxyClient). Wire format:
///   - New (StudyLifeAi:TokenSigningSecret configured): "{userId}.{expiry}.{kid}.{sig}" - the
///     FIRST "kid:secret" entry in the (comma-separated) config signs; studylife-ai looks the
///     secret up by kid to verify, so any entry still configured there can verify a token
///     signed with an older kid (rotation without a simultaneous-redeploy 401 window).
///   - Legacy (StudyLifeAi:TokenSigningSecret unset, StudyLifeAi:SharedSecret set):
///     "{userId}.{expiry}.{sig}" - the original 3-part format, kept only for a rollout window
///     where either side may not have deployed the split yet.
///
/// Deliberately a static helper class instead of a DI service, same reasoning as
/// AuthSessionService: stateless, takes the caller's inputs directly.
/// </summary>
public static class AiProxyTokenService
{
    // Short - only needs to outlive the proxied request itself (including a slow LLM
    // response for /api/ai/agent), not a real session.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>One "kid:secret" entry parsed from StudyLifeAi:TokenSigningSecret.</summary>
    public readonly record struct SigningKey(string Kid, string Secret);

    /// <summary>
    /// Parses "kid1:secret1,kid2:secret2,..." (StudyLifeAi:TokenSigningSecret) into an ordered
    /// list of verification keys - the FIRST entry is the one <see cref="Mint"/> signs new
    /// tokens with (see class doc). Throws <see cref="FormatException"/> on a malformed entry:
    /// a config typo should fail loudly at startup/first use, not silently produce a token
    /// nothing can ever verify.
    /// </summary>
    public static IReadOnlyList<SigningKey> ParseSigningKeys(string config)
    {
        var keys = new List<SigningKey>();
        foreach (var rawEntry in config.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim();
            var colonIndex = entry.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == entry.Length - 1)
            {
                throw new FormatException(
                    $"Malformed StudyLifeAi:TokenSigningSecret entry '{entry}' - expected 'kid:secret'.");
            }
            keys.Add(new SigningKey(entry[..colonIndex], entry[(colonIndex + 1)..]));
        }
        if (keys.Count == 0)
        {
            throw new FormatException(
                "StudyLifeAi:TokenSigningSecret must contain at least one 'kid:secret' entry.");
        }
        return keys;
    }

    /// <summary>Mints the new, key-id-tagged token format: "{userId}.{expiry}.{kid}.{sig}",
    /// always signed with <paramref name="signingKeys"/>[0] (rotation - see class doc).</summary>
    public static string Mint(int userId, IReadOnlyList<SigningKey> signingKeys, DateTime utcNow)
    {
        var signingKey = signingKeys[0];
        var expiry = new DateTimeOffset(utcNow + Lifetime).ToUnixTimeSeconds();
        var payload = $"{userId}.{expiry}";
        return $"{payload}.{signingKey.Kid}.{Sign(payload, signingKey.Secret)}";
    }

    /// <summary>Mints the original, un-keyed token format: "{userId}.{expiry}.{sig}" - used
    /// only as an A5 rollout fallback when StudyLifeAi:TokenSigningSecret isn't configured yet
    /// (see AiProxyClient), so studylife-ai instances that haven't deployed the split can still
    /// verify tokens from a StudyLife instance that has.</summary>
    public static string MintLegacy(int userId, string sharedSecret, DateTime utcNow)
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
