using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Auth;

/// <summary>
/// One single-key API slot on <see cref="AuthUserEntity"/> - the pair of columns
/// (&lt;Name&gt;ApiKeyHash + &lt;Name&gt;ApiKeyCreatedAt) behind one integration's
/// status/generate/revoke trio behind SettingsController, plus the two operations that are
/// allowed to touch them.
///
/// The trios used to be written out once per integration, and by the eighth of them
/// (ha/ai/mcp/capture/focusguard/focustunes/tray/developer) the only thing that differed
/// between two of them was which column pair they assigned - everything security-relevant
/// (CSPRNG token via AuthSessionService.GenerateToken, only ever storing the SHA-256 hash,
/// nulling BOTH columns on revoke) was copy-pasted eight times, which is exactly the shape
/// where one copy eventually drifts. Centralizing it means a new integration adds a descriptor
/// plus three thin endpoints, and can no longer accidentally invent its own hashing or leave a
/// stale CreatedAt behind on revoke.
///
/// Deliberately property accessors rather than reflection or an interface per slot:
/// AuthUserEntity's columns stay plain, individually named properties (that is what the
/// migrations, the unique indexes and StudyLifeAuthenticationHandler's lookups are built on),
/// and a typo in a descriptor is a compile error rather than a runtime surprise.
/// </summary>
public sealed class ApiKeySlot(
    Func<AuthUserEntity, string?> getHash,
    Action<AuthUserEntity, string?> setHash,
    Func<AuthUserEntity, DateTime?> getCreatedAt,
    Action<AuthUserEntity, DateTime?> setCreatedAt)
{
    public string? GetHash(AuthUserEntity user) => getHash(user);

    public DateTime? GetCreatedAt(AuthUserEntity user) => getCreatedAt(user);

    /// <summary>
    /// Issues a fresh key for this slot and stores only its hash, replacing whatever was in the
    /// slot before (the old key gets 401 from the next request onward). The returned PLAINTEXT
    /// is the only copy that will ever exist - same one-time-reveal contract as
    /// AuthSessionService.IssueSession. Caller must SaveChanges.
    /// </summary>
    public string Rotate(AuthUserEntity user, DateTime now)
    {
        var key = AuthSessionService.GenerateToken();
        setHash(user, AuthSessionService.HashToken(key));
        setCreatedAt(user, now);
        return key;
    }

    /// <summary>Permanently clears this slot - both the hash and its timestamp, so a revoked
    /// slot can never report "has no key, but created at ...". Caller must SaveChanges.</summary>
    public void Revoke(AuthUserEntity user)
    {
        setHash(user, null);
        setCreatedAt(user, null);
    }
}

/// <summary>
/// The single-key API slots this server knows about, one per integration. Kept next to
/// <see cref="ApiKeyScopes"/> because the two describe the same slots from opposite sides:
/// this is which COLUMNS a slot owns, that is which ENDPOINTS it may reach.
///
/// studylife-webhooks is deliberately absent: it is the one integration with multiple NAMED
/// keys per user (WebhookApiKeyEntity, its own table with its own rows), not a single column
/// pair, so it has nothing this descriptor could describe.
/// </summary>
public static class ApiKeySlots
{
    /// <summary>Home Assistant (studylife-hacs).</summary>
    public static readonly ApiKeySlot Ha = new(
        u => u.ApiKeyHash, (u, v) => u.ApiKeyHash = v,
        u => u.ApiKeyCreatedAt, (u, v) => u.ApiKeyCreatedAt = v);

    /// <summary>studylife-ai. The only slot whose endpoints do more than write these two
    /// columns - see ApiKeyService.GenerateAiKeyAsync's outbox/registration handling.</summary>
    public static readonly ApiKeySlot Ai = new(
        u => u.AiApiKeyHash, (u, v) => u.AiApiKeyHash = v,
        u => u.AiApiKeyCreatedAt, (u, v) => u.AiApiKeyCreatedAt = v);

    /// <summary>studylife-mcp.</summary>
    public static readonly ApiKeySlot Mcp = new(
        u => u.McpApiKeyHash, (u, v) => u.McpApiKeyHash = v,
        u => u.McpApiKeyCreatedAt, (u, v) => u.McpApiKeyCreatedAt = v);

    /// <summary>studylife-capture browser extension.</summary>
    public static readonly ApiKeySlot Capture = new(
        u => u.CaptureApiKeyHash, (u, v) => u.CaptureApiKeyHash = v,
        u => u.CaptureApiKeyCreatedAt, (u, v) => u.CaptureApiKeyCreatedAt = v);

    /// <summary>studylife-focusguard browser extension.</summary>
    public static readonly ApiKeySlot FocusGuard = new(
        u => u.FocusGuardApiKeyHash, (u, v) => u.FocusGuardApiKeyHash = v,
        u => u.FocusGuardApiKeyCreatedAt, (u, v) => u.FocusGuardApiKeyCreatedAt = v);

    /// <summary>studylife-focustunes browser extension.</summary>
    public static readonly ApiKeySlot FocusTunes = new(
        u => u.FocusTunesApiKeyHash, (u, v) => u.FocusTunesApiKeyHash = v,
        u => u.FocusTunesApiKeyCreatedAt, (u, v) => u.FocusTunesApiKeyCreatedAt = v);

    /// <summary>studylife-tray desktop app.</summary>
    public static readonly ApiKeySlot Tray = new(
        u => u.TrayApiKeyHash, (u, v) => u.TrayApiKeyHash = v,
        u => u.TrayApiKeyCreatedAt, (u, v) => u.TrayApiKeyCreatedAt = v);

    /// <summary>studylife-developers portal. Like Ai, its endpoints add a server-to-server
    /// registration call on top of the column write.</summary>
    public static readonly ApiKeySlot Developer = new(
        u => u.DeveloperApiKeyHash, (u, v) => u.DeveloperApiKeyHash = v,
        u => u.DeveloperApiKeyCreatedAt, (u, v) => u.DeveloperApiKeyCreatedAt = v);
}
