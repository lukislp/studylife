using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Recovery codes - emergency access when a passkey is lost. Moved out of AuthController
/// verbatim, statement for statement: the atomic single-use claim, the prior-session
/// revocation that must happen BEFORE the new session is issued, and the cache eviction that
/// goes with it are all ordering-sensitive (2026-09 audit S7/S13), so this is a move, not a
/// rewrite. The uniform "401 for unknown AND for already used" contract is expressed as a
/// nullable result: <see cref="RecoveryLoginAsync"/> returns null for every rejection, so the
/// controller cannot accidentally distinguish them in its response either.
/// </summary>
public interface IAuthRecoveryService
{
    /// <summary>Generates 8 fresh one-time codes and returns the plaintext EXACTLY ONCE (only
    /// hashes are stored). The user's existing codes are fully invalidated in the process.
    /// The "requires a REAL passkey session" rule stays on the endpoint - a leaked API key must
    /// not be able to construct emergency access for itself.</summary>
    Task<RecoveryCodesResponseDto> GenerateAsync(int userId);

    /// <summary>Status for the setup card: how many codes are still unused, when they were created.</summary>
    Task<RecoveryStatusDto> GetStatusAsync(int userId);

    /// <summary>Redeems a one-time code. Null means "reject" - deliberately the ONLY failure
    /// shape, so "unknown code", "already used" and "lost the concurrent claim" are
    /// indistinguishable to the caller, exactly as before.</summary>
    Task<PasskeyCompleteResponseDto?> RecoveryLoginAsync(string? code);
}

public class AuthRecoveryService(StudyLifeDb db, AuthSessionCache sessionCache) : IAuthRecoveryService
{
    private const int RecoveryCodeCount = 8;
    // Without 0/O/1/I - codes are typed in from paper. 12 characters from a 32-char alphabet = 60 bits.
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<RecoveryCodesResponseDto> GenerateAsync(int userId)
    {
        await db.RecoveryCodes.Where(c => c.AuthUserId == userId).ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var codes = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = string.Concat(Enumerable.Range(0, 12)
                .Select(_ => RecoveryCodeAlphabet[RandomNumberGenerator.GetInt32(RecoveryCodeAlphabet.Length)]));
            codes.Add($"{raw[..4]}-{raw[4..8]}-{raw[8..]}");
            db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                AuthUserId = userId,
                CodeHash = AuthSessionService.HashToken(raw),
                CreatedAt = now,
            });
        }
        await db.SaveChangesAsync();
        return new RecoveryCodesResponseDto { Codes = codes };
    }

    public async Task<RecoveryStatusDto> GetStatusAsync(int userId)
    {
        var codes = await db.RecoveryCodes.AsNoTracking()
            .Where(c => c.AuthUserId == userId).ToListAsync();
        return new RecoveryStatusDto
        {
            TotalCount = codes.Count,
            UnusedCount = codes.Count(c => c.UsedAt == null),
            CreatedAt = codes.Count > 0 ? codes.Max(c => c.CreatedAt) : null,
        };
    }

    public async Task<PasskeyCompleteResponseDto?> RecoveryLoginAsync(string? code)
    {
        var normalized = new string((code ?? "")
            .ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length == 0) return null;

        var hash = AuthSessionService.HashToken(normalized);
        var stored = await db.RecoveryCodes.FirstOrDefaultAsync(c => c.CodeHash == hash && c.UsedAt == null);
        if (stored is null) return null;

        var now = DateTime.UtcNow;
        // Atomic single-use claim (2026-09 audit S13): the read above and this conditional UPDATE
        // are two statements, so two concurrent redemptions of the same code both pass the read -
        // only one of them wins the UPDATE, the other sees 0 rows and gets the same rejection as
        // an already-used code. Same pattern as RegistrationGateService.TryConsumeInviteAsync.
        var claimed = await db.RecoveryCodes
            .Where(c => c.Id == stored.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now));
        if (claimed == 0) return null;
        var user = await db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == stored.AuthUserId);
        // A recovery login IS the "I lost the device that was signed in" case - every session
        // that device (or anyone holding its token) still has must die with it. Done before the
        // new session is issued so the fresh token is the only valid one afterwards (2026-09
        // audit S7).
        var priorSessions = db.AuthSessions.Where(s => s.AuthUserId == stored.AuthUserId);
        var revokedHashes = await priorSessions.Select(s => s.TokenHash).ToListAsync();
        await priorSessions.ExecuteDeleteAsync();
        // Same reasoning as AuthAccountService.RevokeOtherSessionsAsync: the revoked tokens must
        // also leave this pod's AuthSessionCache, not just the table.
        foreach (var revokedHash in revokedHashes) sessionCache.Remove(revokedHash);
        var token = AuthSessionService.IssueSession(db, stored.AuthUserId, now);
        await db.SaveChangesAsync();
        return new PasskeyCompleteResponseDto { Token = token, DisplayName = user?.DisplayName ?? "" };
    }
}
