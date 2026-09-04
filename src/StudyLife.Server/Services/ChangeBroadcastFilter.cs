using Microsoft.AspNetCore.Mvc.Filters;

namespace StudyLife.Server.Services;

/// <summary>
/// Turns every successful write on /api into a change event for the writing user's other
/// clients (GET api/events, see EventsController): increments the user's <see cref="ChangeSequence"/>
/// and publishes the kind derived from the route ("notes" for /api/notes/5, "coursegoals" for
/// /api/coursegoals/31, ...). One global filter instead of a publish call in every controller,
/// so a new controller is covered the day it is added and nothing can be forgotten.
///
/// Sessions and settings writes are additionally signalled by their cache-version counters
/// (needed for cross-pod cache invalidation), so those kinds can arrive twice per write; the
/// client coalesces frames, which keeps this filter simple and complete. Reads, failed writes
/// and the demo instance's 403s publish nothing. Endpoints that do not change user data the
/// pages show (telemetry, auth, push subscriptions, the stream itself, planner proposals,
/// dictation/AI helpers) are excluded.
/// </summary>
public sealed class ChangeBroadcastFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "telemetry", "auth", "push", "events", "planner", "dictate", "ai",
    };

    private readonly ChangeSequence _sequence;
    private readonly IChangeSignal _signal;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ILogger<ChangeBroadcastFilter> _logger;

    public ChangeBroadcastFilter(ChangeSequence sequence, IChangeSignal signal, ICurrentUserAccessor currentUser, ILogger<ChangeBroadcastFilter> logger)
    {
        _sequence = sequence;
        _signal = signal;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var executed = await next();
        var http = context.HttpContext;
        if (executed.Canceled || executed.Exception is not null) return;
        if (!HttpMethods.IsPost(http.Request.Method) && !HttpMethods.IsPut(http.Request.Method)
            && !HttpMethods.IsPatch(http.Request.Method) && !HttpMethods.IsDelete(http.Request.Method)) return;
        if (http.Response.StatusCode is < 200 or >= 300) return;
        // Session and API-key requests alike: an add-on's write (Home Assistant creating a
        // session) must reach the user's own devices too. 0 = no authenticated user (public
        // endpoints such as the demo login) - nothing to notify.
        var userId = _currentUser.AuthUserId;
        if (userId == 0) return;
        var kind = KindFromPath(http.Request.Path);
        if (kind is null || Excluded.Contains(kind)) return;
        try
        {
            await _sequence.IncrementAsync(userId);
            await _signal.PublishAsync(userId, kind);
        }
        catch (Exception ex)
        {
            // Never let the notification fail the write that already succeeded.
            _logger.LogWarning(ex, "change broadcast for {Kind} failed", kind);
        }
    }

    /// <summary>"/api/notes/5" -> "notes"; null for anything that is not an /api route.</summary>
    internal static string? KindFromPath(PathString path)
    {
        var value = path.Value;
        if (value is null || !value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = value.AsSpan(5);
        var slash = rest.IndexOf('/');
        var kind = (slash < 0 ? rest : rest[..slash]).ToString().ToLowerInvariant();
        return kind.Length == 0 ? null : kind;
    }
}
