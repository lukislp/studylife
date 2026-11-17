// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;
using WebPush;

namespace StudyLife.Server.Services;

/// <summary>How one Web Push delivery ended.</summary>
public enum WebPushOutcome
{
    Delivered,
    /// <summary>The push service says the subscription is gone (410): the caller removes it.</summary>
    Expired,
    /// <summary>Any other failure, a timeout or the caller's cancellation (time budget).</summary>
    Failed,
}

/// <summary>
/// The one Web Push transport of the server (2026-11-16 audit M9): every delivery - the worker's
/// reminders and nudges as well as the instant new-record push of SessionService - goes through one
/// WebPushClient over one shared HttpClient with short limits, instead of a new client (and with it
/// a new HttpClient and connection pool) per call and the default 100-second timeout. A push service
/// that answers slowly, or an endpoint that never answers, now costs at most
/// <see cref="RequestTimeout"/> per delivery; the worker additionally caps the time it spends on one
/// user (BackgroundTaskService.PerUserPushBudget).
///
/// Registered as a singleton. The VAPID keys are read on first use: VapidKeysHolder is filled after
/// the container is built (see its remarks).
/// </summary>
public sealed class WebPushDelivery : IDisposable
{
    /// <summary>Whole request, including the push service's answer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>TCP and TLS connect.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly VapidKeysHolder _vapidKeys;
    private readonly HttpClient _http;
    private readonly Lazy<WebPushClient> _client;

    public WebPushDelivery(VapidKeysHolder vapidKeys) : this(vapidKeys, RequestTimeout) { }

    /// <summary>Tests shorten the timeout to prove it holds without waiting ten seconds.</summary>
    internal WebPushDelivery(VapidKeysHolder vapidKeys, TimeSpan requestTimeout)
    {
        _vapidKeys = vapidKeys;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            // Push services are a handful of hosts; rotating the pool picks up their DNS changes.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // A push service answers; it never needs to send the worker somewhere else.
            AllowAutoRedirect = false,
        })
        {
            Timeout = requestTimeout,
        };
        _client = new Lazy<WebPushClient>(CreateClient);
    }

    private WebPushClient CreateClient()
    {
        var keys = _vapidKeys.Keys ?? throw new InvalidOperationException("VAPID keys are not loaded yet.");
        var client = new WebPushClient(_http);
        client.SetVapidDetails(keys.Subject, keys.PublicKey, keys.PrivateKey);
        return client;
    }

    /// <summary>Sends <paramref name="payload"/> to one Web Push subscription. Never throws for a
    /// delivery problem; <paramref name="error"/> carries it for the caller's log.</summary>
    public async Task<(WebPushOutcome Outcome, Exception? Error)> SendAsync(PushSubscriptionEntity subscription, string payload, CancellationToken cancellationToken)
    {
        try
        {
            var target = new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            await _client.Value.SendNotificationAsync(target, payload, (Dictionary<string, object>?)null, cancellationToken);
            return (WebPushOutcome.Delivered, null);
        }
        catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            return (WebPushOutcome.Expired, null);
        }
        catch (Exception ex)
        {
            return (WebPushOutcome.Failed, ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
