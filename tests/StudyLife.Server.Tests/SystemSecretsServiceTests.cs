using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// SystemSecretsService only needs a StudyLifeDb, no full web host - each test builds its
/// own standalone context against its own temp SQLite file, analogous to
/// StudyProgramCatalogTests. Replaces the former VapidKeyProviderTests.cs (file-based) after
/// the switch to DB-backed secrets in the scalability branch (see SystemSecretsService.cs).
/// </summary>
public class SystemSecretsServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-secrets-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        var db = new StudyLifeDb(options, new TestCurrentUserAccessor());
        db.Database.EnsureCreated();
        // busy_timeout: the concurrent race tests below open two connections to the same
        // file and write "simultaneously" - without this, SQLite's single-writer limit would
        // show up as a hard SQLITE_BUSY error instead of a serialized wait (Postgres, the
        // real target system for the multi-process case, does this natively).
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
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

    [Fact]
    public async Task EnsureVapidKeysAsync_FirstRun_GeneratesKeysWithNonLocalhostSubject()
    {
        using var db = NewContext();
        var service = new SystemSecretsService(db);

        var keys = await service.EnsureVapidKeysAsync(new ConfigurationBuilder().Build());

        Assert.DoesNotContain("localhost", keys.Subject);
        Assert.NotEmpty(keys.PublicKey);
        Assert.NotEmpty(keys.PrivateKey);
    }

    [Fact]
    public async Task EnsureVapidKeysAsync_LegacyLocalhostSubjectStored_CorrectsSubjectButKeepsSameKeyPair()
    {
        using (var seedDb = NewContext())
        {
            seedDb.SystemSecrets.Add(new SystemSecretsEntity
            {
                Id = 1,
                VapidSubject = "mailto:studylife@localhost",
                VapidPublicKey = "pub123",
                VapidPrivateKey = "priv456",
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = NewContext();
        var service = new SystemSecretsService(db);
        var keys = await service.EnsureVapidKeysAsync(new ConfigurationBuilder().Build());

        Assert.DoesNotContain("localhost", keys.Subject);
        Assert.Equal("pub123", keys.PublicKey);
        Assert.Equal("priv456", keys.PrivateKey);

        // Migration must persist - a second load shouldn't see the legacy subject again.
        var reloaded = await service.EnsureVapidKeysAsync(new ConfigurationBuilder().Build());
        Assert.Equal(keys.Subject, reloaded.Subject);
        Assert.Equal("pub123", reloaded.PublicKey);
    }

    [Fact]
    public async Task EnsureVapidKeysAsync_ConfigOverrideSet_UsesConfiguredKeysUnchanged()
    {
        using var db = NewContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vapid:PublicKey"] = "configured-pub",
                ["Vapid:PrivateKey"] = "configured-priv",
                ["Vapid:Subject"] = "mailto:ops@example.com",
            })
            .Build();

        var service = new SystemSecretsService(db);
        var keys = await service.EnsureVapidKeysAsync(config);

        Assert.Equal("configured-pub", keys.PublicKey);
        Assert.Equal("configured-priv", keys.PrivateKey);
        Assert.Equal("mailto:ops@example.com", keys.Subject);
        Assert.False(await db.SystemSecrets.AnyAsync());
    }

    [Fact]
    public async Task SetupSecret_EnsureThenValidate_RoundTripsCorrectly()
    {
        using var db = NewContext();
        var service = new SystemSecretsService(db);

        var code = await service.EnsureSetupSecretAsync();

        Assert.True(await service.ValidateSetupSecretAsync(code));
        Assert.False(await service.ValidateSetupSecretAsync("wrong-code"));
        Assert.False(await service.ValidateSetupSecretAsync(null));
    }

    [Fact]
    public async Task ClearSetupSecretAsync_RemovesCode_SubsequentValidateFails()
    {
        using var db = NewContext();
        var service = new SystemSecretsService(db);
        var code = await service.EnsureSetupSecretAsync();

        await service.ClearSetupSecretAsync();

        Assert.False(await service.ValidateSetupSecretAsync(code));
    }

    /// <summary>
    /// Regression test for a bug observed live in the docker-compose.scale.yml test setup:
    /// server and worker containers start practically simultaneously against the same (Postgres)
    /// DB, both saw an empty SystemSecrets row and EACH generated their own VAPID
    /// key pair - without the atomic "only set if still empty" update in
    /// SystemSecretsService, the chronologically last write would have silently
    /// overwritten the other, while both processes kept holding their own (now inconsistent)
    /// key pair in memory. Here two "processes" are simulated as two independent
    /// DbContext instances on the same temp SQLite file, truly calling EnsureVapidKeysAsync
    /// in parallel (Task.WhenAll) - both MUST get back the same key pair.
    /// </summary>
    [Fact]
    public async Task EnsureVapidKeysAsync_TwoConcurrentProcesses_ConvergeOnTheSameKeyPair()
    {
        using var dbA = NewContext();
        using var dbB = NewContext();
        var serviceA = new SystemSecretsService(dbA);
        var serviceB = new SystemSecretsService(dbB);
        var config = new ConfigurationBuilder().Build();

        var results = await Task.WhenAll(
            serviceA.EnsureVapidKeysAsync(config),
            serviceB.EnsureVapidKeysAsync(config));

        Assert.Equal(results[0].PublicKey, results[1].PublicKey);
        Assert.Equal(results[0].PrivateKey, results[1].PrivateKey);
        Assert.Equal(results[0].Subject, results[1].Subject);
    }

    /// <summary>Same race as above, for the setup code (see AuthController.RegisterBegin).</summary>
    [Fact]
    public async Task EnsureSetupSecretAsync_TwoConcurrentProcesses_ConvergeOnTheSameCode()
    {
        using var dbA = NewContext();
        using var dbB = NewContext();
        var serviceA = new SystemSecretsService(dbA);
        var serviceB = new SystemSecretsService(dbB);

        var results = await Task.WhenAll(
            serviceA.EnsureSetupSecretAsync(),
            serviceB.EnsureSetupSecretAsync());

        Assert.Equal(results[0], results[1]);
    }
}
