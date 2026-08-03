namespace StudyLife.Server.Services;

/// <summary>
/// Supplies the AuthUserId of the "current" user for the EF global query filters in
/// StudyLifeDb. Deliberately NOT a header sent by the client as the source (would be freely
/// spoofable) - resolution happens exclusively server-side:
/// - HTTP requests: Program.cs sets HttpContext.Items["AuthUserId"] AFTER the existing
///   API key check, to the id of the (in phase 1, sole) AuthUserEntity.
///   Authorization thus remains exactly as strong as before (possession of the real API key
///   or calendar/share token); a user id is merely resolved additionally.
/// - Background work (BackgroundTaskService): the caller sets the user explicitly
///   per iteration via <see cref="CurrentUserAccessor.BeginBackgroundScope"/> (AsyncLocal,
///   independent of any HTTP request).
/// Phase 2 (passkey login) replaces the HTTP resolution with real session validation,
/// without filters or consumers needing to change.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>Id of the current AuthUserEntity; 0 = no user resolved (query filters
    /// then return empty results, since real ids start at 1).</summary>
    int AuthUserId { get; }
}

public class CurrentUserAccessor : ICurrentUserAccessor
{
    /// <summary>Key for HttpContext.Items, set by the resolution middleware in Program.cs.</summary>
    public const string HttpContextItemKey = "AuthUserId";

    // AsyncLocal instead of an instance field: the BackgroundTaskService is a singleton outside
    // any request scope and must be able to set the user per loop iteration - the
    // value flows via the ExecutionContext into all awaits running underneath it.
    private static readonly AsyncLocal<int?> Ambient = new();

    /// <summary>
    /// Fallback ONLY for contexts without an HTTP request and without an explicit background
    /// scope - in practice: the integration tests that access the DB directly via
    /// factory.Services.CreateScope() (CustomWebApplicationFactory sets the id of the migrated
    /// test user here). In production the value stays null and has no effect whatsoever.
    /// </summary>
    internal static int? AmbientFallbackAuthUserId;

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public int AuthUserId
    {
        get
        {
            if (Ambient.Value is int ambient) return ambient;
            var items = _httpContextAccessor.HttpContext?.Items;
            if (items != null && items.TryGetValue(HttpContextItemKey, out var value) && value is int fromHttp)
                return fromHttp;
            return AmbientFallbackAuthUserId ?? 0;
        }
    }

    /// <summary>
    /// Sets the current user for the duration of the returned scope (Dispose restores
    /// the previous value). Takes priority over HTTP context and fallback.
    /// </summary>
    public static IDisposable BeginBackgroundScope(int authUserId) => new AmbientScope(authUserId);

    private sealed class AmbientScope : IDisposable
    {
        private readonly int? _previous;
        public AmbientScope(int authUserId)
        {
            _previous = Ambient.Value;
            Ambient.Value = authUserId;
        }
        public void Dispose() => Ambient.Value = _previous;
    }
}
