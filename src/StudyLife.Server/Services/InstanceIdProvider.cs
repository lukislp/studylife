using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>The stable id of this installation (= this database): 32 lowercase hex characters,
/// created on first use, persisted, never changed.</summary>
public interface IInstanceIdProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the <see cref="InstanceInfoEntity.InstanceIdKey"/> row, creating it on first use, and caches
/// the value for the process lifetime (it can never change). Race-safe across replicas: the key is
/// the primary key, so when several pods insert at once exactly one wins and the others re-read the
/// winner's row. Singleton; opens its own scope because the DbContext is scoped.
/// </summary>
public sealed class InstanceIdProvider(IServiceScopeFactory scopeFactory) : IInstanceIdProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _cached;

    public static bool IsValidId(string? value) =>
        value is { Length: 32 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached) return cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } again) return again;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            _cached = await ReadOrCreateAsync(db, cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ReadOrCreateAsync(StudyLifeDb db, CancellationToken ct)
    {
        var existing = await ReadAsync(db, ct);
        if (existing is not null) return existing;

        var row = new InstanceInfoEntity { Key = InstanceInfoEntity.InstanceIdKey, Value = Guid.NewGuid().ToString("N") };
        db.InstanceInfo.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Value;
        }
        catch (DbUpdateException)
        {
            // Another replica (or provider instance) inserted first: its row is the id.
            db.Entry(row).State = EntityState.Detached;
            return await ReadAsync(db, ct)
                ?? throw new InvalidOperationException("Could not read or create the instance id row.");
        }
    }

    private static async Task<string?> ReadAsync(StudyLifeDb db, CancellationToken ct)
    {
        var value = await db.InstanceInfo.AsNoTracking()
            .Where(i => i.Key == InstanceInfoEntity.InstanceIdKey)
            .Select(i => i.Value)
            .FirstOrDefaultAsync(ct);
        return IsValidId(value) ? value : null;
    }
}
