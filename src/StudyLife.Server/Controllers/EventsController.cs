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

    public EventsController(IChangeSignal signal) => _signal = signal;

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet]
    public async Task Stream(CancellationToken cancellationToken)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]

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
            await WriteAsync(": connected\n\n", cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = events.Reader.ReadAsync(cancellationToken).AsTask();
                var heartbeat = Task.Delay(HeartbeatInterval, cancellationToken);
                var finished = await Task.WhenAny(next, heartbeat);
                if (finished == next)
                    await WriteAsync($"event: change\ndata: {await next}\n\n", cancellationToken);
                else
                    await WriteAsync(": ping\n\n", cancellationToken);
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

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(text, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
