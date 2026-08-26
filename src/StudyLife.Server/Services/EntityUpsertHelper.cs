using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Get-or-create for "one row per user" tables (UserSettingsEntity, TimerStateEntity) that are
/// query-filtered by AuthUserId but have no in-app synchronization of their own. Without this,
/// two concurrent first-writes for the same user (e.g. two tabs opened at once, both PUTting
/// settings before either row exists) could both pass FirstOrDefaultAsync as null and both
/// insert - after which every later FirstOrDefaultAsync nondeterministically picks either
/// duplicate. Same claim-first shape as SystemSecretsService.GetOrCreateRowAsync/
/// BackgroundTaskService.TryClaimReminderAsync: rely on the DB's unique index on AuthUserId
/// (see StudyLifeDb.OnModelCreating) as the actual lock - the loser's insert throws
/// DbUpdateException and simply re-reads the winner's row instead of duplicating it.
/// </summary>
public static class EntityUpsertHelper
{
    public static async Task<T> GetOrCreateAsync<T>(this DbSet<T> set, StudyLifeDb db) where T : class, new()
    {
        var existing = await set.FirstOrDefaultAsync();
        if (existing != null) return existing;

        var entity = new T();
        set.Add(entity);
        try
        {
            await db.SaveChangesAsync();
            return entity;
        }
        catch (DbUpdateException)
        {
            db.Entry(entity).State = EntityState.Detached;
            return await set.FirstOrDefaultAsync()
                ?? throw new InvalidOperationException($"Could not read or create the {typeof(T).Name} row.");
        }
    }
}
