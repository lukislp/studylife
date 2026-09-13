using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The per-user API-key slots behind /api/settings/&lt;slot&gt;-api-key: status, generate, revoke,
/// plus the multi-key studylife-webhooks registration list. Every method takes the calling
/// user's id explicitly instead of reading it itself: the id comes from
/// HttpContext.SessionAuthUserId(), which only exists because those endpoints are
/// [Authorize(SessionOnly)] - resolving it is the controller's job, acting on it is this
/// service's. The two slots that do more than write their own columns (ai, developer: outbox +
/// server-to-server registration) get their own methods; the other six share the three generic
/// ones, driven by an <see cref="ApiKeySlot"/> descriptor.
/// </summary>
public interface IApiKeyService
{
    /// <summary>Existence + timestamp of a slot's key, never the plaintext (like a password it
    /// is only visible once at generation). Takes the mapper rather than the slot, because each
    /// slot's status DTO is its own wire type and SetupController's bundle endpoint reuses the
    /// same mappers.</summary>
    Task<ServiceResult<TDto>> GetStatusAsync<TDto>(int userId, Func<AuthUserEntity, TDto> toDto);

    /// <summary>Issues a fresh key into that slot only (every other slot untouched), immediately
    /// invalidating whatever was in it, and hands back the plaintext the one and only time it
    /// exists.</summary>
    Task<ServiceResult<TDto>> GenerateAsync<TDto>(int userId, ApiKeySlot slot, Func<string, DateTime, TDto> toDto);

    /// <summary>Permanently clears that slot (the integration gets 401 from its next request
    /// onward), all other slots untouched.</summary>
    Task<ServiceResult> RevokeAsync(int userId, ApiKeySlot slot);

    Task<ServiceResult<AiApiKeyGenerateResponseDto>> GenerateAiKeyAsync(int userId, CancellationToken ct);
    Task<ServiceResult> RevokeAiKeyAsync(int userId, CancellationToken ct);
    Task<ServiceResult<DeveloperApiKeyGenerateResponseDto>> GenerateDeveloperKeyAsync(int userId, CancellationToken ct);
    Task<ServiceResult> RevokeDeveloperKeyAsync(int userId, CancellationToken ct);

    Task<List<WebhookApiKeyDto>> GetWebhookKeysAsync(int userId);
    Task<ServiceResult<CreateWebhookApiKeyResponseDto>> CreateWebhookKeyAsync(int userId, CreateWebhookApiKeyRequestDto request);
    Task<ServiceResult> DeleteWebhookKeyAsync(int userId, int id);
}

public class ApiKeyService(
    StudyLifeDb db,
    AiProxyClient aiProxyClient,
    DeveloperProxyClient developerProxyClient) : IApiKeyService
{
    public async Task<ServiceResult<TDto>> GetStatusAsync<TDto>(int userId, Func<AuthUserEntity, TDto> toDto)
    {
        var user = await db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult<TDto>.Unauthorized();
        return ServiceResult<TDto>.Success(toDto(user));
    }

    public async Task<ServiceResult<TDto>> GenerateAsync<TDto>(int userId, ApiKeySlot slot, Func<string, DateTime, TDto> toDto)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult<TDto>.Unauthorized();

        var key = slot.Rotate(user, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return ServiceResult<TDto>.Success(toDto(key, slot.GetCreatedAt(user)!.Value));
    }

    public async Task<ServiceResult> RevokeAsync(int userId, ApiKeySlot slot)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult.Unauthorized();

        slot.Revoke(user);
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-ai (this slot only -
    /// Home Assistant's key is untouched) and registers the plaintext with studylife-ai
    /// (AiProxyClient.RegisterKeyAsync) at the one moment it exists - see docs/decisions.md
    /// "M4.5 Multi-user support" in the studylife-ai repo, "Registration-on-generate":
    /// studylife-ai cannot retrieve it later, only the hash is ever stored here. AI key outbox
    /// (audit A7): the intent is durably enqueued BEFORE the immediate delivery attempt, so a
    /// studylife-ai outage right now doesn't lose the plaintext forever - on confirmed delivery
    /// the row is deleted immediately (fast path, identical behavior to before the outbox
    /// existed); otherwise BackgroundTaskService.RunAiKeyOutboxAsync retries it with backoff.
    /// Either way key generation itself never fails because of this.</summary>
    public async Task<ServiceResult<AiApiKeyGenerateResponseDto>> GenerateAiKeyAsync(int userId, CancellationToken ct)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult<AiApiKeyGenerateResponseDto>.Unauthorized();

        var key = ApiKeySlots.Ai.Rotate(user, DateTime.UtcNow);
        var outboxRow = new AiKeyOutboxEntity
        {
            AuthUserId = userId,
            Action = AiKeyOutboxEntity.ActionRegister,
            AiApiKeyPlaintext = key,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiKeyOutbox.Add(outboxRow);
        await db.SaveChangesAsync();

        if (await aiProxyClient.RegisterKeyAsync(userId, key, ct))
        {
            db.AiKeyOutbox.Remove(outboxRow);
        }
        else
        {
            // Record this as the row's first attempt (same bookkeeping RunAiKeyOutboxAsync does
            // for every retry), so the background drain's backoff starts counting from here
            // instead of treating the fast-path attempt as if it never happened.
            outboxRow.Attempts = 1;
            outboxRow.LastAttemptAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return ServiceResult<AiApiKeyGenerateResponseDto>.Success(
            new AiApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.AiApiKeyCreatedAt!.Value });
    }

    /// <summary>Permanently revokes the studylife-ai API key (hash is deleted) - studylife-ai
    /// gets 401 from the next request onward. Home Assistant's key is untouched. Also tells
    /// studylife-ai to forget its registered copy (AiProxyClient.RevokeKeyAsync) - without
    /// this, a revoked-here key would keep working there indefinitely. Same outbox-first pattern
    /// as GenerateAiKeyAsync above, so an unreachable studylife-ai doesn't leave the two
    /// databases disagreeing forever (audit A7).</summary>
    public async Task<ServiceResult> RevokeAiKeyAsync(int userId, CancellationToken ct)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult.Unauthorized();

        ApiKeySlots.Ai.Revoke(user);
        var outboxRow = new AiKeyOutboxEntity
        {
            AuthUserId = userId,
            Action = AiKeyOutboxEntity.ActionRevoke,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiKeyOutbox.Add(outboxRow);
        await db.SaveChangesAsync();

        if (await aiProxyClient.RevokeKeyAsync(userId, ct))
        {
            db.AiKeyOutbox.Remove(outboxRow);
        }
        else
        {
            outboxRow.Attempts = 1;
            outboxRow.LastAttemptAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    /// <summary>The studylife-developers portal slot - a toggle, not a reveal-once-plaintext
    /// lifecycle: nothing external ever needs the raw key, the server hands it to
    /// studylife-developers itself (DeveloperProxyClient.RegisterKeyAsync). Deliberately no
    /// outbox here (see that client's own class doc) - a failed delivery simply surfaces as a
    /// non-success response, the user retries the toggle.</summary>
    public async Task<ServiceResult<DeveloperApiKeyGenerateResponseDto>> GenerateDeveloperKeyAsync(int userId, CancellationToken ct)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult<DeveloperApiKeyGenerateResponseDto>.Unauthorized();

        var key = ApiKeySlots.Developer.Rotate(user, DateTime.UtcNow);
        await db.SaveChangesAsync();

        await developerProxyClient.RegisterKeyAsync(userId, key, ct);
        return ServiceResult<DeveloperApiKeyGenerateResponseDto>.Success(
            new DeveloperApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.DeveloperApiKeyCreatedAt!.Value });
    }

    public async Task<ServiceResult> RevokeDeveloperKeyAsync(int userId, CancellationToken ct)
    {
        var user = await db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return ServiceResult.Unauthorized();

        ApiKeySlots.Developer.Revoke(user);
        await db.SaveChangesAsync();

        await developerProxyClient.RevokeKeyAsync(userId, ct);
        return ServiceResult.Success();
    }

    // Unlike every other slot above (one key per user, a column on AuthUserEntity), the
    // studylife-webhooks registration-management slot supports multiple NAMED keys per user
    // (WebhookApiKeyEntity) - one per external program/add-on the user wants to let manage its
    // own webhook subscriptions (see ApiKeyScopes.Webhooks), not one single known consumer.
    // WebhookApiKeys carries no query filter (see StudyLifeDb's own comment on it), so every
    // method here filters explicitly by AuthUserId - the same "user-specific accesses filter
    // explicitly" pattern that entity's comment documents.

    public Task<List<WebhookApiKeyDto>> GetWebhookKeysAsync(int userId) => LoadWebhookApiKeysAsync(db, userId);

    // internal instead of private: reused by SetupController (bundle endpoint), same rationale
    // as StudyProgramService.LoadSummariesAsync.
    internal static async Task<List<WebhookApiKeyDto>> LoadWebhookApiKeysAsync(StudyLifeDb db, int userId) =>
        await db.WebhookApiKeys.AsNoTracking()
            .Where(k => k.AuthUserId == userId)
            .OrderBy(k => k.CreatedAt)
            .Select(k => new WebhookApiKeyDto { Id = k.Id, Name = k.Name, CreatedAt = k.CreatedAt })
            .ToListAsync();

    public async Task<ServiceResult<CreateWebhookApiKeyResponseDto>> CreateWebhookKeyAsync(int userId, CreateWebhookApiKeyRequestDto request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return ServiceResult<CreateWebhookApiKeyResponseDto>.Invalid("Name must not be empty.");
        if (name.Length > 100) return ServiceResult<CreateWebhookApiKeyResponseDto>.Invalid("Name must be at most 100 characters long.");

        var key = AuthSessionService.GenerateToken();
        var entity = new WebhookApiKeyEntity
        {
            AuthUserId = userId,
            Name = name,
            KeyHash = AuthSessionService.HashToken(key),
            CreatedAt = DateTime.UtcNow,
        };
        db.WebhookApiKeys.Add(entity);
        await db.SaveChangesAsync();
        return ServiceResult<CreateWebhookApiKeyResponseDto>.Success(
            new CreateWebhookApiKeyResponseDto { Id = entity.Id, Name = entity.Name, ApiKey = key, CreatedAt = entity.CreatedAt });
    }

    public async Task<ServiceResult> DeleteWebhookKeyAsync(int userId, int id)
    {
        var entity = await db.WebhookApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.AuthUserId == userId);
        if (entity is null) return ServiceResult.NotFound();
        db.WebhookApiKeys.Remove(entity);
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    // ── Status DTO mappers, one per slot ─────────────────────────────────────────────────────
    // internal instead of private: each is used both by its own status endpoint and by
    // SetupController's bundle endpoint, so both call sites map the same AuthUserEntity fields
    // the same way.

    internal static HaApiKeyStatusDto ToHaApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.ApiKeyHash != null, CreatedAt = user.ApiKeyCreatedAt };

    internal static AiApiKeyStatusDto ToAiApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.AiApiKeyHash != null, CreatedAt = user.AiApiKeyCreatedAt };

    internal static McpApiKeyStatusDto ToMcpApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.McpApiKeyHash != null, CreatedAt = user.McpApiKeyCreatedAt };

    internal static CaptureApiKeyStatusDto ToCaptureApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.CaptureApiKeyHash != null, CreatedAt = user.CaptureApiKeyCreatedAt };

    internal static FocusGuardApiKeyStatusDto ToFocusGuardApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.FocusGuardApiKeyHash != null, CreatedAt = user.FocusGuardApiKeyCreatedAt };

    internal static FocusTunesApiKeyStatusDto ToFocusTunesApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.FocusTunesApiKeyHash != null, CreatedAt = user.FocusTunesApiKeyCreatedAt };

    internal static TrayApiKeyStatusDto ToTrayApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.TrayApiKeyHash != null, CreatedAt = user.TrayApiKeyCreatedAt };

    internal static DeveloperApiKeyStatusDto ToDeveloperApiKeyStatusDto(AuthUserEntity user) =>
        new() { HasKey = user.DeveloperApiKeyHash != null, CreatedAt = user.DeveloperApiKeyCreatedAt };

    // ── Slot rotations as method groups for the consent connect flows ────────────────────────
    // AuthController's connect actions (identity contract v1 §2 step 3) provision these slots
    // without going through the generate endpoints, and pass one of these as `rotateKey` - so
    // the connect flow and this service's own generate path provably rotate the same slot the
    // same way, never with duplicated hashing. Caller must SaveChanges.

    internal static string RotateMcpKey(AuthUserEntity user, DateTime now) => ApiKeySlots.Mcp.Rotate(user, now);
    internal static string RotateCaptureKey(AuthUserEntity user, DateTime now) => ApiKeySlots.Capture.Rotate(user, now);
    internal static string RotateFocusGuardKey(AuthUserEntity user, DateTime now) => ApiKeySlots.FocusGuard.Rotate(user, now);
    internal static string RotateFocusTunesKey(AuthUserEntity user, DateTime now) => ApiKeySlots.FocusTunes.Rotate(user, now);
    internal static string RotateTrayKey(AuthUserEntity user, DateTime now) => ApiKeySlots.Tray.Rotate(user, now);
}
