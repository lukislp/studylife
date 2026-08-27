using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Recovery codes (emergency access when a passkey is lost) ────────────────

    private const int RecoveryCodeCount = 8;
    // Without 0/O/1/I - codes are typed in from paper. 12 characters from a 32-char alphabet = 60 bits.
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Generates 8 fresh one-time codes and returns the plaintext EXACTLY ONCE (only hashes
    /// are stored in the DB). Requires a REAL passkey session - a leaked API key must not be
    /// able to construct emergency access for itself (same rationale as for ha-api-key/calendar
    /// token). The user's existing codes are fully invalidated in the process.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("recovery/generate")]
    public async Task<ActionResult<RecoveryCodesResponseDto>> GenerateRecoveryCodes()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]

        await _db.RecoveryCodes.Where(c => c.AuthUserId == userId).ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var codes = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = string.Concat(Enumerable.Range(0, 12)
                .Select(_ => RecoveryCodeAlphabet[RandomNumberGenerator.GetInt32(RecoveryCodeAlphabet.Length)]));
            codes.Add($"{raw[..4]}-{raw[4..8]}-{raw[8..]}");
            _db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                AuthUserId = userId,
                CodeHash = AuthSessionService.HashToken(raw),
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync();
        return new RecoveryCodesResponseDto { Codes = codes };
    }

    /// <summary>Status for the setup card: how many codes are still unused, when they were created.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("recovery/status")]
    public async Task<ActionResult<RecoveryStatusDto>> GetRecoveryStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var codes = await _db.RecoveryCodes.AsNoTracking()
            .Where(c => c.AuthUserId == userId).ToListAsync();
        return new RecoveryStatusDto
        {
            TotalCount = codes.Count,
            UnusedCount = codes.Count(c => c.UsedAt == null),
            CreatedAt = codes.Count > 0 ? codes.Max(c => c.CreatedAt) : null,
        };
    }

    /// <summary>
    /// Emergency login with a one-time code (unauthenticated like login/begin - codes are
    /// exactly the way back in without a passkey). The hash identifies the user directly
    /// (unique index); uniformly 401 for both "unknown" and "already used". Brute force is
    /// throttled via its own strict rate-limit partition in Program.cs.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("recovery/login")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> RecoveryLogin([FromBody] RecoveryLoginRequestDto request)
    {
        var normalized = new string((request.Code ?? "")
            .ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length == 0) return Unauthorized();

        var hash = AuthSessionService.HashToken(normalized);
        var code = await _db.RecoveryCodes.FirstOrDefaultAsync(c => c.CodeHash == hash && c.UsedAt == null);
        if (code is null) return Unauthorized();

        var now = DateTime.UtcNow;
        code.UsedAt = now;
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == code.AuthUserId);
        var token = AuthSessionService.IssueSession(_db, code.AuthUserId, now);
        await _db.SaveChangesAsync();
        return new PasskeyCompleteResponseDto { Token = token, DisplayName = user?.DisplayName ?? "" };
    }
}
