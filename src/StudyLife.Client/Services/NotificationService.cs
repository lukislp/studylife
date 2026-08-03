using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StudyLife.Client.Services;

public class NotificationService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly HttpClient _http;
    private readonly INativePush _nativePush;
    private bool _initialized;

    public NotificationService(IJSRuntime js, HttpClient http, INativePush nativePush)
    {
        _js = js;
        _http = http;
        _nativePush = nativePush;
    }

    /// <summary>
    /// Must be called explicitly via a button click - browsers only allow
    /// requestPermission() as a reaction to a user gesture.
    /// </summary>
    public async Task<string> RequestPermissionAsync()
    {
        return await _js.InvokeAsync<string>("requestNotificationPermission");
    }

    /// <summary>
    /// Requests permission (if needed) and registers the push subscription.
    /// Only call after an explicit user click.
    /// </summary>
    public async Task InitializeAsync()
    {
        var permission = await RequestPermissionAsync();
        if (permission != "granted") return;

        if (_initialized) return;
        _initialized = true;

        // Native app shell with APNs capability (paid signing): register a device token instead
        // of a web push subscription. Always false in the browser - web flow unchanged.
        if (_nativePush.IsAvailable)
        {
            await _nativePush.RegisterAsync();
            return;
        }

        try
        {
            var result = await _http.GetFromJsonAsync<VapidPublicKeyResponse>("api/push/publickey");
            if (result?.PublicKey == null) return;

            var subJson = await _js.InvokeAsync<string?>("subscribePush", result.PublicKey);
            if (subJson == null) return;

            var sub = JsonSerializer.Deserialize<PushSubPayload>(subJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (sub == null) return;

            await _http.PostAsJsonAsync("api/push/subscribe", sub);
        }
        catch
        {
            // No network or push not supported - silently ignore
        }
    }

    /// <summary>
    /// Returns the current notification permission status (without requesting it).
    /// </summary>
    public async Task<string> GetPermissionStatusAsync()
    {
        return await _js.InvokeAsync<string>("getNotificationPermissionStatus");
    }

    /// <summary>
    /// Sends a local test notification via the service worker.
    /// </summary>
    public async Task SendTestNotificationAsync()
    {
        await _js.InvokeVoidAsync("sendTestNotification");
    }

    /// <summary>
    /// Unsubscribes THIS browser's push subscription and immediately re-subscribes - without a
    /// renewed permission dialog, since the browser permission exists independently of the
    /// subscription and has already been granted. Necessary because subscribePush() on an
    /// existing (even dead/orphaned) subscription always just returns the existing one instead
    /// of creating a new one - e.g. after the server's VAPID key has changed and push delivery
    /// has since been failing with "VAPID credentials ... do not correspond to the credentials
    /// used to create the subscriptions". The only way to get a valid subscription again in
    /// this case without the user having to touch browser settings themselves.
    /// </summary>
    public async Task<bool> ResubscribeAsync()
    {
        // APNs has no concept of a VAPID key change - re-subscribe here simply means:
        // register the token again (idempotent, also refreshes the device entry).
        if (_nativePush.IsAvailable)
            return await _nativePush.RegisterAsync();

        try
        {
            await _js.InvokeVoidAsync("unsubscribePush");

            var result = await _http.GetFromJsonAsync<VapidPublicKeyResponse>("api/push/publickey");
            if (result?.PublicKey == null) return false;

            var subJson = await _js.InvokeAsync<string?>("subscribePush", result.PublicKey);
            if (subJson == null) return false;

            var sub = JsonSerializer.Deserialize<PushSubPayload>(subJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (sub == null) return false;

            var response = await _http.PostAsJsonAsync("api/push/subscribe", sub);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unsubscribes THIS browser's push subscription purely locally, without re-subscribing - for
    /// the case where the user has removed their own device via the device manager
    /// (PushDeviceManager): the server entry is then already gone, but the browser's
    /// subscribePush() would otherwise keep returning the same (now orphaned) subscription.
    /// </summary>
    public async Task UnsubscribeLocalAsync()
    {
        // APNs channel: there's no local subscription state to clean up (the token belongs to
        // the operating system); the caller has already handled removing the server entry.
        if (_nativePush.IsAvailable) return;

        try
        {
            await _js.InvokeVoidAsync("unsubscribePush");
        }
        catch
        {
            // No service worker/push not supported - nothing to do
        }
    }

    /// <summary>
    /// Shows a local notification (not a push) - e.g. for timer events.
    /// </summary>
    public async Task ShowLocalNotificationAsync(string title, string body)
    {
        await _js.InvokeVoidAsync("showLocalNotification", title, body);
    }

    /// <summary>
    /// Returns the SHA256 hash of THIS browser's push subscription (identical to the
    /// server-side computation in PushController.HashEndpoint), so the device list
    /// (PushDeviceManager) can mark "this device" without transmitting the real endpoint.
    /// Calls subscribePush again - that's idempotent (returns the existing
    /// subscription instead of creating a new one), so it's unproblematic here. Null if
    /// no permission has been granted or push isn't supported/initialized.
    /// </summary>
    public async Task<string?> GetCurrentEndpointHashAsync()
    {
        if (_nativePush.IsAvailable)
            return await _nativePush.GetEndpointHashAsync();

        try
        {
            var permission = await GetPermissionStatusAsync();
            if (permission != "granted") return null;

            var result = await _http.GetFromJsonAsync<VapidPublicKeyResponse>("api/push/publickey");
            if (result?.PublicKey == null) return null;

            var subJson = await _js.InvokeAsync<string?>("subscribePush", result.PublicKey);
            if (subJson == null) return null;

            var sub = JsonSerializer.Deserialize<PushSubPayload>(subJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (sub == null) return null;

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sub.Endpoint));
            return Convert.ToHexString(hashBytes);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask;
    }

    private record VapidPublicKeyResponse(string PublicKey);
    private record PushSubPayload(string Endpoint, string P256dh, string Auth);
}

