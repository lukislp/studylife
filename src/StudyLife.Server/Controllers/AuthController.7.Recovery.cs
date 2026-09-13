using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Recovery codes (emergency access when a passkey is lost) ────────────────
    // Generation/status/redemption live in AuthRecoveryService; what stays here is which policy
    // each endpoint runs under and the uniform 401 the redemption answers with.

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
        return await _recovery.GenerateAsync(userId);
    }

    /// <summary>Status for the setup card: how many codes are still unused, when they were created.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("recovery/status")]
    public async Task<ActionResult<RecoveryStatusDto>> GetRecoveryStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        return await _recovery.GetStatusAsync(userId);
    }

    /// <summary>
    /// Emergency login with a one-time code (unauthenticated like login/begin - codes are
    /// exactly the way back in without a passkey). Uniformly 401 for every rejection:
    /// AuthRecoveryService.RecoveryLoginAsync reports failure as a bare null precisely so
    /// "unknown", "already used" and "lost the concurrent claim" cannot be told apart here
    /// either. Brute force is throttled via its own strict rate-limit partition in Program.cs.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("recovery/login")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> RecoveryLogin([FromBody] RecoveryLoginRequestDto request)
    {
        var result = await _recovery.RecoveryLoginAsync(request.Code);
        if (result is null) return Unauthorized();
        return result;
    }
}
