using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Demo mode (public read-only demo instances) ──────────────────────────

    /// <summary>True only when DEMO_MODE=true AND the confirmation guard also passes - see
    /// DemoModeGuard. Never true on a normal deployment.</summary>
    private bool DemoModeEnabled => DemoModeGuard.IsEnabled(_config);

    /// <summary>Lets the client discover at login time whether this is a public demo
    /// instance (Login.razor then auto-signs-in via demo-login instead of showing the
    /// passkey UI). Deliberately reveals nothing else - on a normal instance this simply
    /// returns demo:false and the login page behaves exactly as before.</summary>
    [AllowAnonymous]
    [HttpGet("demo")]
    public ActionResult<DemoInfoDto> GetDemoInfo() => new DemoInfoDto { Demo = DemoModeEnabled };

    /// <summary>
    /// Passwordless auto-login for public demo instances: issues a REAL session (same
    /// AuthSessionService path as a passkey login) for the demo user, so every other part
    /// of the auth machinery - token header, sliding expiry, 401 handling - runs on the
    /// completely normal code path. Hard-disabled (404) unless DEMO_MODE=true; on a demo
    /// instance the write-block middleware in Program.cs allows exactly this one non-GET
    /// path through, while every other mutation (including passkey registration, so nobody
    /// can create themselves an account on the demo) is rejected with 403.
    /// The demo user is the first AuthUser row - seeded once at deployment time.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("demo-login")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> DemoLogin()
    {
        if (!DemoModeEnabled) return NotFound();

        var session = await _account.IssueDemoSessionAsync();
        if (session is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "demo user not seeded" });
        return session;
    }
}
