using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Single source of truth for "is this AuthUser the instance owner" (audit finding A15/A2-family
/// fix) - replaces the two previously duplicated "lowest Id wins" checks in
/// BackupController.IsOwnerAsync and AuthController.GetAccountInfo. Ownership itself now lives on
/// AuthUserEntity.IsOwner (a real column, see StudyLifeDb.cs), so a raw DB restore carries the
/// flag along deterministically instead of re-deriving it from whatever insertion order the
/// restored rows happen to have.
/// </summary>
public interface IOwnershipService
{
    /// <summary>True if authUserId is the instance owner. False for 0/unknown users.</summary>
    Task<bool> IsOwnerAsync(int authUserId);
}

public class OwnershipService(StudyLifeDb db, ILogger<OwnershipService> logger) : IOwnershipService
{
    public async Task<bool> IsOwnerAsync(int authUserId)
    {
        if (authUserId <= 0) return false;

        var isOwner = await db.AuthUsers.Where(u => u.Id == authUserId).Select(u => (bool?)u.IsOwner).FirstOrDefaultAsync();
        if (isOwner is null) return false; // no such user
        if (isOwner.Value) return true;

        // Self-healing fallback: normally every DB has exactly one IsOwner=true row (either from
        // the AddAuthUserIsOwner backfill migration, or set explicitly at first registration/demo
        // seeding - see AuthController.RegisterComplete/DemoSeeder). The only way to reach a state
        // with NO owner at all is a raw restore (BackupController) of a backup taken from a DB
        // that predates this feature (migrations already ran and backfilled against a DIFFERENT
        // dataset before the restore file existed) - or manual DB surgery. Rather than leaving the
        // instance ownerless, fall back ONCE to the original insertion-order rule and PERSIST it,
        // so every later call hits the fast path above instead of re-deriving it every request.
        if (await db.AuthUsers.AnyAsync(u => u.IsOwner)) return false; // someone else already holds it

        var lowestId = await db.AuthUsers.OrderBy(u => u.Id).Select(u => u.Id).FirstOrDefaultAsync();
        if (lowestId == 0) return false; // no AuthUsers at all - should never happen past setup

        await db.AuthUsers.Where(u => u.Id == lowestId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOwner, true));
        logger.LogWarning(
            "No AuthUser had IsOwner set - self-healed by assigning ownership to the lowest-Id user (Id={UserId}). " +
            "This is expected once after restoring a pre-ownership-flag backup; unexpected otherwise.", lowestId);
        return authUserId == lowestId;
    }
}
