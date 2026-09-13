using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Everything an already-authenticated account does to ITS OWN credentials, sessions and issued
/// add-on keys - the persistence half of AuthController.9.Devices.cs, plus the demo instance's
/// standing session (the only other place a session is minted outside a passkey ceremony or a
/// recovery login). The WebAuthn ceremonies themselves stay in the controller: they are built
/// around per-request Fido2 configuration and the distributed challenge cache, and their writes
/// are inseparable from the verification that precedes them.
///
/// Every method takes the calling session's ids explicitly (user id, and where it matters the
/// current session id / token hash) instead of reading HttpContext: those values exist only
/// because [Authorize(SessionOnly)] put them there, so resolving them is the controller's job.
/// </summary>
public interface IAuthAccountService
{
    /// <summary>Deletes exactly the caller's own session row and evicts its token from this
    /// pod's cache - the explicit sign-out must be immediate here, whatever the other pods do
    /// (they drop their entries at the cache's own TTL).</summary>
    Task LogoutAsync(int sessionId, string? sessionTokenHash);

    Task<List<PasskeyListItemDto>> ListCredentialsAsync(int userId);

    /// <summary>Approves a passkey created via register/begin-additional - idempotent, so an
    /// already-approved credential is a no-op success rather than an error.</summary>
    Task<ServiceResult> ApproveCredentialAsync(int userId, int credentialId);

    Task<ServiceResult> RenameCredentialAsync(int userId, int credentialId, string? label);

    /// <summary>Deletes a passkey and, with it, every session of the account except the caller's
    /// own - removing a passkey usually means "that device is gone" (2026-09 audit S7). Refuses
    /// to delete the account's LAST approved passkey: there is no password fallback, so that
    /// would lock the user out permanently once their sessions expire.</summary>
    Task<ServiceResult> DeleteCredentialAsync(int userId, int credentialId, int currentSessionId);

    /// <summary>"Sign out everywhere else": deletes every session of the account except the one
    /// making the call, and evicts the revoked tokens from this pod's cache.</summary>
    Task<int> RevokeOtherSessionsAsync(int userId, int currentSessionId);

    Task<List<ClientApiKeyListItemDto>> ListClientKeysAsync(int userId);

    Task<ServiceResult> RevokeClientKeyAsync(int userId, int keyId);

    /// <summary>Issues a session for the demo instance's seeded user. Null = no AuthUser row
    /// exists at all, which the endpoint reports as 503 ("demo user not seeded"). The DEMO_MODE
    /// gate itself stays on the endpoint - it decides whether the route exists at all.</summary>
    Task<PasskeyCompleteResponseDto?> IssueDemoSessionAsync();
}

public class AuthAccountService(StudyLifeDb db, AuthSessionCache sessionCache) : IAuthAccountService
{
    public async Task LogoutAsync(int sessionId, string? sessionTokenHash)
    {
        await db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync();
        // The handler's per-pod session cache (AuthSessionCache) would otherwise keep answering
        // for this token for up to 30 more seconds on THIS pod.
        if (sessionTokenHash is not null) sessionCache.Remove(sessionTokenHash);
    }

    public async Task<List<PasskeyListItemDto>> ListCredentialsAsync(int userId) =>
        await db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyListItemDto
            {
                Id = c.Id,
                DeviceLabel = c.DeviceLabel,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
                Pending = c.ApprovedAt == null,
            })
            .ToListAsync();

    public async Task<ServiceResult> ApproveCredentialAsync(int userId, int credentialId)
    {
        var credential = await db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.AuthUserId == userId);
        if (credential is null) return ServiceResult.NotFound();
        if (credential.ApprovedAt is not null) return ServiceResult.Success(); // already approved, idempotent

        credential.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    public async Task<ServiceResult> RenameCredentialAsync(int userId, int credentialId, string? label)
    {
        var credential = await db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.AuthUserId == userId);
        if (credential is null) return ServiceResult.NotFound();

        var trimmed = (label ?? "").Trim();
        if (trimmed.Length > 100) return ServiceResult.Invalid("Label must be at most 100 characters long.");
        credential.DeviceLabel = trimmed.Length == 0 ? null : trimmed;
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }

    public async Task<ServiceResult> DeleteCredentialAsync(int userId, int credentialId, int currentSessionId)
    {
        var credential = await db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.AuthUserId == userId);
        if (credential is null) return ServiceResult.NotFound();

        // Deleting the last passkey would permanently lock the user out once their sessions
        // expire (there is no password fallback) - deliberately blocked. Only APPROVED
        // passkeys count toward this: a still-pending additional passkey (reject case) is
        // useless for login anyway and must never prevent deletion of the only real access
        // method.
        var ownApprovedCount = await db.PasskeyCredentials.CountAsync(c => c.AuthUserId == userId && c.ApprovedAt != null);
        if (credential.ApprovedAt is not null && ownApprovedCount <= 1)
            return ServiceResult.Invalid("The last passkey of an account cannot be removed.");

        db.PasskeyCredentials.Remove(credential);
        await db.SaveChangesAsync();
        // Removing a passkey usually means "that device is gone / no longer trusted" - the
        // sessions it holds must not outlive the credential by up to 180 days. Only the caller's
        // own session survives (2026-09 audit S7).
        await RevokeOtherSessionsAsync(userId, currentSessionId);
        return ServiceResult.Success();
    }

    public async Task<int> RevokeOtherSessionsAsync(int userId, int currentSessionId)
    {
        var others = db.AuthSessions.Where(s => s.AuthUserId == userId && s.Id != currentSessionId);
        // Evict the revoked tokens from this pod's AuthSessionCache as well - otherwise a device
        // signed out here could keep authenticating against THIS pod for up to 30 more seconds.
        // Other pods drop their entries at the cache's TTL (documented on AuthSessionCache).
        var revokedHashes = await others.Select(s => s.TokenHash).ToListAsync();
        var deleted = await others.ExecuteDeleteAsync();
        foreach (var hash in revokedHashes) sessionCache.Remove(hash);
        return deleted;
    }

    public Task<List<ClientApiKeyListItemDto>> ListClientKeysAsync(int userId) => LoadClientKeysAsync(db, userId);

    // internal instead of private: reused by SetupController (bundle endpoint), which holds its
    // own StudyLifeDb.
    internal static async Task<List<ClientApiKeyListItemDto>> LoadClientKeysAsync(StudyLifeDb db, int userId)
    {
        var keys = await db.ClientApiKeys.AsNoTracking()
            .Where(k => k.AuthUserId == userId)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync();
        var clientIds = keys.Select(k => k.ClientId).Distinct().ToList();
        var names = await db.OAuthClients.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId))
            .ToDictionaryAsync(c => c.ClientId, c => c.Name);
        return keys.Select(k => new ClientApiKeyListItemDto
        {
            Id = k.Id,
            ClientId = k.ClientId,
            ClientName = names.GetValueOrDefault(k.ClientId),
            GrantedScopes = ApiKeyScopes.Parse(k.GrantedScopes).Select(e => $"{e.Controller}.{e.Action}").OrderBy(s => s).ToList(),
            CreatedAt = k.CreatedAt,
        }).ToList();
    }

    public async Task<ServiceResult> RevokeClientKeyAsync(int userId, int keyId)
    {
        var deleted = await db.ClientApiKeys.Where(k => k.Id == keyId && k.AuthUserId == userId).ExecuteDeleteAsync();
        return deleted == 0 ? ServiceResult.NotFound() : ServiceResult.Success();
    }

    public async Task<PasskeyCompleteResponseDto?> IssueDemoSessionAsync()
    {
        var user = await db.AuthUsers.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync();
        if (user is null) return null;

        var now = DateTime.UtcNow;
        // Same opportunistic cleanup as LoginComplete: a public demo issues a session per
        // visitor, so expired rows would otherwise accumulate with nothing else pruning them.
        await db.AuthSessions.Where(s => s.ExpiresAt <= now || s.HardExpiresAt <= now).ExecuteDeleteAsync();
        var token = AuthSessionService.IssueSession(db, user.Id, now);
        await db.SaveChangesAsync();
        return new PasskeyCompleteResponseDto { Token = token, DisplayName = user.DisplayName };
    }
}
