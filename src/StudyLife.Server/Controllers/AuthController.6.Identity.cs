using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Identity resolution (identity contract v1 §1) ───────────────────────

    /// <summary>
    /// Lets a satellite (studylife-mcp, studylife-ai, Home Assistant, studylife-capture) resolve
    /// the REAL AuthUserId behind whatever credential it holds, instead of inventing its own
    /// identity (e.g. studylife-mcp's former sha256(api key) OAuth subject - audit finding A1).
    /// Unlike the rest of this controller, this endpoint deliberately does NOT carry
    /// [AllowAnonymous] - it requires the default ApiAccess policy (a real credential, session
    /// OR any of the four key slots) so it can report which one actually matched, via
    /// AuthSessionService.ApiKeySlotItemKey (only the API-key branch of the AuthenticationHandler
    /// sets that item).
    /// </summary>
    [HttpGet("whoami")]
    public ActionResult<WhoamiResponseDto> Whoami()
    {
        if (HttpContext.Items[CurrentUserAccessor.HttpContextItemKey] is not int userId)
            return Unauthorized(); // defensive only - the gate already rejects anything without a resolved user

        var credential = HttpContext.Items.ContainsKey(AuthSessionService.SessionItemKey)
            ? "session"
            : HttpContext.Items[AuthSessionService.ApiKeySlotItemKey] as string;
        if (credential is null) return Unauthorized();

        return new WhoamiResponseDto { UserId = userId, Credential = credential };
    }

    // ── Session-required management ────────────────────────────────────────

    /// <summary>
    /// Generates a short-lived link code for one's own account (session-required - only an
    /// already logged-in device may issue one). For register/begin-linked: the code replaces
    /// the account mapping that begin-additional otherwise reads from the session there. The
    /// new device still ends up PENDING after Complete and additionally still requires an
    /// explicit approval via the device list - the code only proves "which account", not
    /// "this is genuinely trustworthy" (see RegisterComplete/LoginComplete).
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("link/begin")]
    public async Task<ActionResult<DeviceLinkCodeResponseDto>> LinkBegin()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]

        var displayCode = GenerateLinkCode();
        await CacheSetAsync(LinkCodeCacheKey(displayCode), new PendingDeviceLink(userId), LinkCodeLifetime);
        return new DeviceLinkCodeResponseDto { Code = displayCode, ExpiresInSeconds = (int)LinkCodeLifetime.TotalSeconds };
    }
}
