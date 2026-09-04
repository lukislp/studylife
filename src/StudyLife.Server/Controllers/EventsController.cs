using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Server-sent events stream of "your data changed" notifications for the logged-in user - the
/// push half of the cross-client sync. The client (AppStateService via js/interop.js) keeps one
/// of these open and refetches sessions/settings the moment an event arrives instead of only
/// on its 30-second poll; the poll stays as the fallback for browsers/hosts where the stream
/// cannot be held open. Events carry only the kind ("sessions"/"settings"), never data - the
/// refetch goes through the normal authenticated API, so this endpoint adds no new data path.
///
/// Session-only: an API key has no UI to push to. Heartbeat comments every 25 seconds keep
/// intermediaries (the gateway's read timeout, Cloudflare on the demo host) from closing an idle
/// stream. Not cached (no-cache) and not compressed (text/event-stream is not in the response
/// compression MIME list), and buffering is disabled so each event is flushed immediately.
/// </summary>
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);

    private readonly IChangeSignal _signal;
    private readonly SessionHistoryCacheVersion _historyVersion;
    private readonly SettingsCacheVersion _settingsVersion;

    public EventsController(IChangeSignal signal, SessionHistoryCacheVersion historyVersion, SettingsCacheVersion settingsVersion)
    {
        _signal = signal;
        _historyVersion = historyVersion;
        _settingsVersion = settingsVersion;
    }

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet]
    public async Task Stream(CancellationToken cancellationToken, [FromQuery] int v = 1)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        // v=2 (2026-09): every frame carries the user's two cache-version counters (the same ones
        // that key the server-side caches and are bumped on every session/settings write). With
        // them the client no longer has to poll to know whether it is current: the connect frame
        // and each 25 s heartbeat say so for free, and a reconnect after a dropped connection
        // reconciles in one comparison instead of two blind GETs. v=1 keeps the old shape
        // (": connected", ": ping" comments, "event: change" with the bare kind) for clients that
        // were deployed before this - for them heartbeats stay comments they ignore.
        var versioned = v >= 2;

        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        using var subscription = _signal.Subscribe(userId, kind => events.Writer.TryWrite(kind));

        StudyLifeMetrics.SseStreamsStarted.Add(1);
        StudyLifeMetrics.SseStreamsOpen.Add(1);
        try
        {
            if (versioned)
                await WriteAsync($"event: state\ndata: {await StateJsonAsync(userId, null)}\n\n", cancellationToken);
            else
                await WriteAsync(": connected\n\n", cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = events.Reader.ReadAsync(cancellationToken).AsTask();
                var heartbeat = Task.Delay(HeartbeatInterval, cancellationToken);
                var finished = await Task.WhenAny(next, heartbeat);
                if (finished == next)
                {
                    var kind = await next;
                    await WriteAsync(versioned
                        ? $"event: change\ndata: {await StateJsonAsync(userId, kind)}\n\n"
                        : $"event: change\ndata: {kind}\n\n", cancellationToken);
                }
                else
                {
                    await WriteAsync(versioned
                        ? $"event: state\ndata: {await StateJsonAsync(userId, null)}\n\n"
                        : ": ping\n\n", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away - the normal end of every stream, nothing to report.
        }
        finally
        {
            StudyLifeMetrics.SseStreamsOpen.Add(-1);
        }
    }

    /// <summary>v=2 frame payload: the two per-user cache versions the client compares against
    /// what it last saw, plus the kind that triggered a change frame (null on connect/heartbeat).
    /// Read at send time, i.e. after the write's Bump, so a change frame already carries the new
    /// value. Serialized by hand: three fields, no need for a DTO the client would also have to
    /// carry - the shape is documented on ChangeStateFrame in the client.</summary>
    private async Task<string> StateJsonAsync(int userId, string? kind)
    {
        var history = await _historyVersion.GetAsync(userId);
        var settings = await _settingsVersion.GetAsync(userId);
        var kindJson = kind is null ? "null" : System.Text.Json.JsonSerializer.Serialize(kind);
        return $"{{\"kind\":{kindJson},\"historyVersion\":{history},\"settingsVersion\":{settings}}}";
    }

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(text, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
