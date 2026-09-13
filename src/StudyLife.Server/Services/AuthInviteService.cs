using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Registration invites (audit finding A10): the persistence behind the three owner-only
/// /api/auth/invites endpoints. Deliberately owns NO access control - the owner check stays in
/// AuthController, where it has to answer 403 rather than 401 (see the comment on its
/// IsOwnerAsync), and where the "query vs. access control are separate concerns" split has
/// always lived. Consuming an invite is NOT here either: that happens inside
/// RegisterComplete's transaction, via IRegistrationGateService.TryConsumeInviteAsync.
/// </summary>
public interface IAuthInviteService
{
    /// <summary>Creates an invite and returns the PLAINTEXT token exactly once - only its
    /// SHA-256 hash is persisted (AuthInviteEntity.TokenHash), the same one-time-visibility
    /// pattern as the API-key slots and the recovery codes.</summary>
    Task<CreateInviteResponseDto> CreateAsync(int createdByUserId);

    /// <summary>Every invite, never the token itself - only enough for the setup UI to show
    /// created/expires/used state per row.</summary>
    Task<List<InviteListItemDto>> ListAsync();

    /// <summary>Permanently deletes an invite - works on unused, used, and expired rows alike
    /// (simple cleanup/revoke, no separate "revoke" vs. "delete" distinction).</summary>
    Task<ServiceResult> DeleteAsync(int id);
}

public class AuthInviteService(StudyLifeDb db) : IAuthInviteService
{
    public async Task<CreateInviteResponseDto> CreateAsync(int createdByUserId)
    {
        var now = DateTime.UtcNow;
        var token = AuthSessionService.GenerateToken();
        var invite = new AuthInviteEntity
        {
            TokenHash = AuthSessionService.HashToken(token),
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            ExpiresAt = now + RegistrationGateService.InviteLifetime,
        };
        db.AuthInvites.Add(invite);
        await db.SaveChangesAsync();

        return new CreateInviteResponseDto { Id = invite.Id, Token = token, CreatedAt = invite.CreatedAt, ExpiresAt = invite.ExpiresAt };
    }

    public Task<List<InviteListItemDto>> ListAsync() => LoadInvitesAsync(db);

    // internal instead of private: reused by SetupController (bundle endpoint), which holds its
    // own StudyLifeDb - the owner check stays at each call site rather than moving in here, same
    // split as everywhere else around these endpoints.
    internal static async Task<List<InviteListItemDto>> LoadInvitesAsync(StudyLifeDb db) =>
        await db.AuthInvites.AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListItemDto { Id = i.Id, CreatedAt = i.CreatedAt, ExpiresAt = i.ExpiresAt, UsedAt = i.UsedAt })
            .ToListAsync();

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var invite = await db.AuthInvites.FirstOrDefaultAsync(i => i.Id == id);
        if (invite is null) return ServiceResult.NotFound();

        db.AuthInvites.Remove(invite);
        await db.SaveChangesAsync();
        return ServiceResult.Success();
    }
}
