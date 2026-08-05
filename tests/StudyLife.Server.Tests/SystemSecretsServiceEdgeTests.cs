using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Deterministic simulations of the multi-pod startup races that SystemSecretsService's
/// "only set if still empty" SQL guards exist for. The parallel Task.WhenAll tests in
/// SystemSecretsServiceTests prove convergence but cannot force WHICH process loses - so the
/// loser branches (rowsAffected == 0, PK conflict on first-ever insert) stay untested there.
/// Here a one-shot DbCommandInterceptor plays the "other pod": right before the service's own
/// guarded statement executes, a side connection writes the winner state, making THIS process
/// deterministically lose the race.
/// </summary>
public class SystemSecretsServiceEdgeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-secrets-edge-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}");
        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
        var db = new StudyLifeDb(builder.Options, new TestCurrentUserAccessor());
        db.Database.EnsureCreated();
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    /// <summary>Simulates the concurrent "other pod": executes SQL over its own,
    /// non-pooled connection to the same file, independent of EF's connection.</summary>
    private void RunAsOtherPod(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Fires <paramref name="beforeExecute"/> exactly once, right before the first
    /// command whose text matches - the seam between the service's read and its guarded write.</summary>
    private sealed class OneShotCommandHook(Func<DbCommand, bool> match, Action beforeExecute) : DbCommandInterceptor
    {
        private bool _fired;

        private void MaybeFire(DbCommand command)
        {
            if (_fired || !match(command)) return;
            _fired = true;
            beforeExecute();
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            MaybeFire(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeFire(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeFire(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            MaybeFire(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task EnsureVapidKeysAsync_LosesTheGenerationRace_AdoptsTheWinnersKeyPair()
    {
        // The other pod persists its key pair between our empty-read and our guarded UPDATE -
        // our freshly generated pair must be discarded and the winner's pair returned, or this
        // process would sign pushes with a private key that doesn't match the stored public key.
        var hook = new OneShotCommandHook(
            cmd => cmd.CommandText.Contains("\"VapidPublicKey\" IS NULL"),
            () => RunAsOtherPod(
                "UPDATE \"SystemSecrets\" SET \"VapidPublicKey\" = 'winner-pub', " +
                "\"VapidPrivateKey\" = 'winner-priv', \"VapidSubject\" = 'mailto:winner@example.com' " +
                "WHERE \"Id\" = 1;"));

        using var db = NewContext(hook);
        var service = new SystemSecretsService(db);

        var keys = await service.EnsureVapidKeysAsync(new ConfigurationBuilder().Build());

        Assert.Equal("winner-pub", keys.PublicKey);
        Assert.Equal("winner-priv", keys.PrivateKey);
        Assert.Equal("mailto:winner@example.com", keys.Subject);
    }

    [Fact]
    public async Task EnsureSetupSecretAsync_LosesTheGenerationRace_ReturnsTheWinnersCode()
    {
        // Same seam for the setup code: the operator must see ONE code no matter which
        // container's logs they read, so the losing process returns the winner's code.
        var hook = new OneShotCommandHook(
            cmd => cmd.CommandText.Contains("\"SetupSecretCode\" IS NULL"),
            () => RunAsOtherPod(
                "UPDATE \"SystemSecrets\" SET \"SetupSecretCode\" = 'WINR-CODE' WHERE \"Id\" = 1;"));

        using var db = NewContext(hook);
        var service = new SystemSecretsService(db);

        var code = await service.EnsureSetupSecretAsync();

        Assert.Equal("WINR-CODE", code);
        Assert.True(await service.ValidateSetupSecretAsync("WINR-CODE"));
    }

    [Fact]
    public async Task GetOrCreateRow_LosesTheVeryFirstInsertRace_ReadsTheWinnersRowInstead()
    {
        // First-ever startup race: the other pod inserts the fixed Id-1 row (already carrying
        // its setup code) after our not-found read but before our own INSERT. The PK conflict
        // must be swallowed (DbUpdateException catch), the winner's row re-read, and the
        // winner's code returned - not a crash, and not a second conflicting attempt.
        var hook = new OneShotCommandHook(
            cmd => cmd.CommandText.Contains("INSERT INTO \"SystemSecrets\""),
            () => RunAsOtherPod(
                "INSERT INTO \"SystemSecrets\" (\"Id\", \"VapidPublicKey\", \"VapidPrivateKey\", \"VapidSubject\", \"SetupSecretCode\") " +
                "VALUES (1, NULL, NULL, NULL, 'RACE-CODE');"));

        using var db = NewContext(hook);
        var service = new SystemSecretsService(db);

        var code = await service.EnsureSetupSecretAsync();

        Assert.Equal("RACE-CODE", code);
        Assert.Equal(1, await db.SystemSecrets.AsNoTracking().CountAsync()); // still exactly one row
    }
}
