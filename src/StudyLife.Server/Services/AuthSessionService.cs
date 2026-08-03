using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Session token logic for passkey login (phase 2): generating, hashing, and validating with
/// sliding extension. Deliberately a static helper class instead of a DI service -
/// all methods are stateless and take the caller's DbContext (gate middleware in
/// Program.cs / AuthController), so there is exactly one implementation of the expiry rules.
///
/// Lifetime model: ExpiresAt slides to "now + 90 days" on every valid request,
/// HardExpiresAt (= IssuedAt + 180 days) is NEVER exceeded by this - a user who uses the app
/// daily must therefore log in fresh via passkey at least every 180 days; the same applies
/// to someone who doesn't open it for 90 days at all.
/// </summary>
public static class AuthSessionService
{
    public static readonly TimeSpan SlidingWindow = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(180);

    /// <summary>HttpContext.Items key: id of the validated AuthSessionEntity. Only set
    /// when the request came in with a VALID X-Session-Token - the AuthController uses this
    /// to check whether session-required endpoints (logout, device list, extra passkey) really
    /// come from a logged-in session instead of just the shared API key.</summary>
    public const string SessionItemKey = "AuthSessionId";

    /// <summary>Header through which the client sends its session token (sibling of X-Api-Key).</summary>
    public const string TokenHeaderName = "X-Session-Token";

    // 32 bytes CSPRNG as base64url - exactly the same format as ApiKeyProvider.GenerateKey,
    // so tokens are header-safe without escaping.
    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>SHA-256 of the plaintext token as lowercase hex - only this hash sits in the DB,
    /// so a DB leak doesn't expose usable tokens.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a new session for the user and returns the PLAINTEXT token -
    /// the only moment it exists. The caller must call SaveChanges themselves.</summary>
    public static string IssueSession(StudyLifeDb db, int authUserId, DateTime utcNow)
    {
        var token = GenerateToken();
        db.AuthSessions.Add(new AuthSessionEntity
        {
            AuthUserId = authUserId,
            TokenHash = HashToken(token),
            IssuedAt = utcNow,
            ExpiresAt = utcNow + SlidingWindow,
            HardExpiresAt = utcNow + MaxLifetime,
            LastUsedAt = utcNow,
        });
        return token;
    }

    /// <summary>The deviation above which the sliding extension is even worth it - prevents
    /// a DB write on EVERY single authenticated request (relevant write wear on the
    /// Raspberry Pi SD card). With daily usage, the real sliding window thus stays
    /// at most one hour behind the theoretical optimum - irrelevant
    /// compared to the 90-day window.</summary>
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromHours(1);

    /// <summary>
    /// Validates a token against the AuthSessions table and extends the session on a sliding
    /// basis (ExpiresAt = now + 90 days, capped at HardExpiresAt). Null = invalid/expired.
    /// The write access for the extension happens immediately here (its own SaveChanges), so the
    /// middleware doesn't couple a half-updated state to subsequent controller saves -
    /// but only if ExpiresAt would actually shift by more than RefreshDebounce as a result.
    /// </summary>
    public static async Task<AuthSessionEntity?> ValidateAndRefreshAsync(StudyLifeDb db, string? token, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.TokenHash == hash);
        if (session is null) return null;
        if (session.ExpiresAt <= utcNow || session.HardExpiresAt <= utcNow) return null;

        var refreshed = utcNow + SlidingWindow;
        var cappedRefreshed = refreshed < session.HardExpiresAt ? refreshed : session.HardExpiresAt;
        if (cappedRefreshed - session.ExpiresAt > RefreshDebounce)
        {
            session.ExpiresAt = cappedRefreshed;
            session.LastUsedAt = utcNow;
            await db.SaveChangesAsync();
        }
        return session;
    }
}
