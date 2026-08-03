namespace StudyLife.Client.Services;

/// <summary>
/// Additive push-channel hook for native app shells (same pattern as INativeAppAuth):
/// if a native implementation is available (iOS app with the aps-environment entitlement, i.e.
/// paid signing - see the studylife-app repo), NotificationService registers this
/// device via an APNs token through api/push/subscribe-apns instead of the web push subscription.
/// In the browser (NoNativePush) IsAvailable is always false and the existing
/// VAPID web push flow runs exactly as before.
/// </summary>
public interface INativePush
{
    bool IsAvailable { get; }

    /// <summary>Fetches the APNs device token from the operating system and registers it with the
    /// server (api/push/subscribe-apns). Idempotent - repeated calls only refresh the
    /// device entry. False on failure (e.g. no permission, no network).</summary>
    Task<bool> RegisterAsync();

    /// <summary>SHA256 hex over the synthetic endpoint "apns:&lt;token&gt;" - identical to the
    /// server-side hash computation for the device list, so the UI can mark "this device".
    /// Null if no token exists (yet).</summary>
    Task<string?> GetEndpointHashAsync();
}

/// <summary>Default registration in the browser client: no native channel exists there.</summary>
public sealed class NoNativePush : INativePush
{
    public bool IsAvailable => false;
    public Task<bool> RegisterAsync() => Task.FromResult(false);
    public Task<string?> GetEndpointHashAsync() => Task.FromResult<string?>(null);
}
