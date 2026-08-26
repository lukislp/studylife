using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Audit finding O6: every existing probe (k8s readiness/liveness for both web and worker,
/// docker-compose.scale healthchecks, the Dockerfile's own HEALTHCHECK) used to hit plain
/// GET / - which only proves Kestrel itself is up, not that the app can actually talk to its
/// database/pooler. A pod whose Postgres connection (or pooler) has died therefore stayed
/// "Ready" forever, kept receiving traffic, and every request failed. Two dedicated,
/// deliberately cheap endpoints replace that:
///
/// - GET /healthz/live: liveness. No dependencies at all (not even the DB) - answers as soon
///   as Kestrel can route a request, exactly the "is the process alive and not deadlocked"
///   question a liveness probe should ask. Never fails for a reason a container restart
///   couldn't fix (a dead DB connection is NOT such a reason - restarting the pod doesn't
///   revive a dead database, so DB failures must only ever affect READINESS, never trigger a
///   liveness-driven restart loop). Used for BOTH probes on the worker (k8s/05-worker.yaml) -
///   the worker has no Service/routing, so "Ready" has no meaning there, and it must not be
///   killed by a false liveness failure while its own wait-for-migrations startup loop
///   (Program.cs, WaitForPendingMigrationsAsync) can legitimately keep Kestrel from listening
///   at all for up to 5 minutes (see the startupProbe on that Deployment).
/// - GET /healthz/ready: readiness. Runs a trivial DB round-trip (SELECT 1) through EF Core -
///   exactly the "can this pod actually serve real requests right now" question a readiness
///   probe should ask, so a dead DB/pooler connection takes the pod out of rotation instead of
///   silently 500ing every real request. 503 (not an exception bubbling up as 500) on failure -
///   the expected/documented "not ready yet" signal a probe polls for, not an error condition.
///
/// Deliberately OUTSIDE /api (both routes below the app root, not under [Route("api/...")]):
/// two independent, load-bearing reasons, not just a style choice.
/// 1. Program.cs's rate limiter partitions everything under /api at 300 req/min per client IP
///    (kube-probe traffic reaches the pod directly, not via the ingress/nginx hop that populates
///    X-Forwarded-For, so its "IP" is the probing node's address - many pods on the same node
///    would otherwise share one partition key and could throttle each other's probes). Living
///    outside /api means these endpoints hit RateLimitPartition.GetNoLimiter unconditionally
///    (see the GlobalLimiter's "!StartsWithSegments(/api)" branch) - no throttling to reason
///    about at all, for any traffic source.
/// 2. Program.cs's demo-mode write-block middleware and the "unknown /api path -> 404" fallback
///    both key off the /api prefix too - living outside it means probes are automatically
///    unaffected by either, no special-casing needed there.
///
/// [AllowAnonymous] is REQUIRED, not optional, on both actions: AuthorizationOptions.
/// FallbackPolicy (ApiAccess, see StudyLifeAuthorizationPolicies) applies to every endpoint with
/// no authorization metadata of its own, regardless of path - without it, kubelet's probe
/// (which never sends a session token or API key) would get 401 and the pod would never become
/// Ready/pass liveness. Same pattern as the Apple site-association endpoint and MapOpenApi() in
/// Program.cs.
/// </summary>
[ApiController]
[Route("healthz")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public HealthController(StudyLifeDb db) => _db = db;

    /// <summary>
    /// Liveness: deliberately dependency-free (see the class comment for why a DB check has no
    /// place here). Response body is empty on purpose - a probe only looks at the status code.
    /// </summary>
    [HttpGet("live")]
    public IActionResult Live()
    {
        // Same reasoning as SystemController.GetCapabilities: a probe response must never end
        // up in an HTTP cache (irrelevant for kube-probe itself, but curl-based docker
        // healthchecks/compose share the same code path and a stale cached 200 would be
        // actively misleading here).
        Response.Headers.CacheControl = "no-store";
        return Ok();
    }

    /// <summary>
    /// Readiness: SELECT 1 through the already-configured DbContext (SQLite or Postgres, via
    /// the pooler in k8s - whichever this process is actually configured for, no provider
    /// branching needed here). ExecuteSqlRawAsync (not CanConnectAsync): CanConnectAsync merely
    /// opens+closes a connection and can succeed against some proxies/poolers even when they'd
    /// fail to actually execute a query - a real (if trivial) query round-trip is the more
    /// faithful "can this pod serve a real request" signal, and costs nothing extra measurable.
    /// 503 (ServiceUnavailable), not letting the exception bubble into the default exception
    /// handler's 500: this is the expected, POLLED-FOR "not ready right now" signal a readiness
    /// probe is designed around, not an application error to log/alert on the way a real request
    /// failure would be.
    /// </summary>
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return Ok();
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
