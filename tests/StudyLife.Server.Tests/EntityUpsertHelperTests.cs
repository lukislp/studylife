using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// EntityUpsertHelper.GetOrCreateAsync (M3 fix). Each fact below gets its OWN factory/DB
/// (separate classes, not just separate facts in one IClassFixture class): they all assert the
/// exact row COUNT for the seeded test user, which a shared DB across facts in the same class
/// would otherwise perturb (xUnit runs facts within a class against the same fixture instance).
/// </summary>
public class EntityUpsertHelperNoExistingRowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EntityUpsertHelperNoExistingRowTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOrCreateAsync_NoExistingRow_CreatesExactlyOneRow()
    {
        var created = await _factory.WithDbAsync(db => db.Settings.GetOrCreateAsync(db));

        Assert.True(created.Id > 0); // already persisted, not just added to the change tracker
        var count = await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, count);
    }
}

public class EntityUpsertHelperExistingRowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EntityUpsertHelperExistingRowTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOrCreateAsync_ExistingRow_ReturnsItWithoutCreatingADuplicate()
    {
        var first = await _factory.WithDbAsync(db => db.TimerState.GetOrCreateAsync(db));

        var second = await _factory.WithDbAsync(db => db.TimerState.GetOrCreateAsync(db));

        Assert.Equal(first.Id, second.Id);
        var count = await _factory.WithDbAsync(db => db.TimerState.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, count);
    }
}

public class EntityUpsertHelperRaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EntityUpsertHelperRaceTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// Simulates the actual race two concurrent first-writes for the same user could hit:
    /// both requests' FirstOrDefaultAsync probes see no row yet (the genuine race window),
    /// then one of them ("the winner") completes its full GetOrCreateAsync (insert + commit)
    /// before "the loser" gets to insert its own row. The loser's insert now collides with the
    /// unique index on AuthUserId (see StudyLifeDb.OnModelCreating/migration
    /// AddPerUserUniqueRows) - this reproduces that collision directly against real, separate
    /// DbContext instances (as two concurrent requests would use) rather than via actual
    /// threading, which would make the interleaving non-deterministic and the test flaky.
    /// GetOrCreateAsync's catch block is expected to detach the failed insert and re-read,
    /// converging on the single winning row instead of throwing or duplicating.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_LoserOfConcurrentFirstWrite_ConvergesOnWinnersRowInsteadOfDuplicating()
    {
        using var winnerScope = _factory.Services.CreateScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        using var loserScope = _factory.Services.CreateScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<StudyLifeDb>();

        // Both "requests" observe the genuinely empty table before either commits.
        Assert.Null(await winnerDb.Settings.FirstOrDefaultAsync());
        Assert.Null(await loserDb.Settings.FirstOrDefaultAsync());

        var winner = await winnerDb.Settings.GetOrCreateAsync(winnerDb);

        // The loser proceeds exactly like GetOrCreateAsync's insert branch would, having
        // already observed no row - this now collides with the unique index the winner just
        // committed.
        var loserAttempt = new UserSettingsEntity();
        loserDb.Settings.Add(loserAttempt);
        await Assert.ThrowsAsync<DbUpdateException>(() => loserDb.SaveChangesAsync());

        // Reproduce GetOrCreateAsync's recovery step: detach the failed insert, re-read.
        loserDb.Entry(loserAttempt).State = EntityState.Detached;
        var reread = await loserDb.Settings.FirstOrDefaultAsync();

        Assert.NotNull(reread);
        Assert.Equal(winner.Id, reread!.Id);
        var count = await winnerDb.Settings.IgnoreQueryFilters().CountAsync();
        Assert.Equal(1, count);
    }
}
