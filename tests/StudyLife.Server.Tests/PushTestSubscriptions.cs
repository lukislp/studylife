using StudyLife.Server.Data;

namespace StudyLife.Server.Tests;

/// <summary>
/// Inserts a web-push subscription for the default test user (AuthUserId 1) straight into the
/// DB. The delivery tests point subscriptions at fake push servers listening on
/// http://127.0.0.1:&lt;port&gt; - since PushController.Subscribe only accepts public https
/// endpoints (OutboundUrlPolicy, 2026-09 audit S4), those can no longer be registered through
/// the API and have to bypass it here. Only the registration path changes; what the tests
/// exercise (the worker/controller POSTing to the endpoint and reacting to its status) is
/// untouched.
/// </summary>
internal static class PushTestSubscriptions
{
    public static Task InsertAsync(CustomWebApplicationFactory factory, string endpoint, string p256dh, string auth, int authUserId = 1) =>
        factory.WithDbAsync(db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                AuthUserId = authUserId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChangesAsync();
        });
}
