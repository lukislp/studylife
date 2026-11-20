// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;
using StudyLife.Server.Services.Erasure;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>The journal file itself: append, read back, prune, tolerate damage.</summary>
public class FileErasureJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }

    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Record_ThenRead_ReturnsEntriesOldestFirst_AndHoldsNothingButTheEntry()
    {
        var journal = new FileErasureJournal(_path);
        journal.Record(new ErasureEntry(ErasureKind.AiConsentWithdrawn, 7, T0.AddDays(-30), T0.AddDays(2)));
        journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, 5, T0.AddDays(-60), T0));

        var entries = journal.Read();

        Assert.Equal([5, 7], entries.Select(e => e.UserId));
        Assert.Equal(ErasureKind.AccountDeleted, entries[0].Kind);
        // Four properties per line: kind, user id, creation time, time of the erasure.
        var line = File.ReadAllLines(_path)[0];
        Assert.Equal(4, System.Text.Json.JsonDocument.Parse(line).RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Read_SkipsUnreadableLines()
    {
        var journal = new FileErasureJournal(_path);
        journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, 1, T0, T0));
        File.AppendAllText(_path, "not json\n");
        journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, 2, T0, T0));

        Assert.Equal([1, 2], journal.Read().Select(e => e.UserId));
    }

    [Fact]
    public void Prune_RemovesOnlyEntriesOlderThanTheCutoff()
    {
        var journal = new FileErasureJournal(_path);
        journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, 1, T0, T0.AddDays(-100)));
        journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, 2, T0, T0.AddDays(-10)));

        Assert.Equal(1, journal.Prune(T0.AddDays(-90)));
        Assert.Equal([2], journal.Read().Select(e => e.UserId));
        Assert.Equal(0, journal.Prune(T0.AddDays(-90)));
    }

    [Fact]
    public void MissingFile_ReadsEmpty_AndPruneIsANoOp()
    {
        var journal = new FileErasureJournal(_path);
        Assert.Empty(journal.Read());
        Assert.Equal(0, journal.Prune(T0));
    }
}

/// <summary>
/// The journal wired into the app: erasures are recorded, and the reconciler applies them to a
/// database that went back in time. A restore is simulated by putting the rows back the way a
/// backup would hold them (same id, same creation time); the restore swap itself is covered by
/// BackupRestoreTests. The reconciler runs on EF Core only, so SQLite and Postgres behave alike.
/// </summary>
public class ErasureReconcilerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ErasureReconcilerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.CreateClient(); // starts the host (migration, seeded user 1)
    }

    private IErasureJournal Journal => _factory.Services.GetRequiredService<IErasureJournal>();

    private async Task<(int Id, DateTime CreatedAt)> AddUserAsync(Action<UserSettingsEntity>? settings = null)
    {
        var createdAt = DateTime.UtcNow.AddDays(-20);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var user = new AuthUserEntity { DisplayName = "journal-test", CreatedAt = createdAt };
        db.AuthUsers.Add(user);
        await db.SaveChangesAsync();
        if (settings is not null)
        {
            var row = new UserSettingsEntity { AuthUserId = user.Id };
            settings(row);
            db.Settings.Add(row);
            await db.SaveChangesAsync();
        }
        return (user.Id, user.CreatedAt);
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<ErasureReconcileResult> ReconcileAsync() =>
        InScope(sp => sp.GetRequiredService<ErasureReconciler>().RunAsync(DateTime.UtcNow));

    private Task<bool> UserExistsAsync(int id) =>
        _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == id));

    [Fact]
    public async Task DeletingAnAccount_WritesAJournalEntry_WithIdAndCreationTimeOnly()
    {
        var (id, createdAt) = await AddUserAsync();

        var result = await InScope(sp => sp.GetRequiredService<IAccountDeletionService>().DeleteAsync(id, id, default));

        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        var entry = Assert.Single(Journal.Read(), e => e.UserId == id);
        Assert.Equal(ErasureKind.AccountDeleted, entry.Kind);
        Assert.Equal(createdAt, entry.UserCreatedAt);
    }

    [Fact]
    public async Task AccountBroughtBackByARestore_IsDeletedAgain_AndASecondPassChangesNothing()
    {
        var (id, createdAt) = await AddUserAsync();
        await InScope(sp => sp.GetRequiredService<IAccountDeletionService>().DeleteAsync(id, id, default));
        Assert.False(await UserExistsAsync(id));

        // The backup still holds the account.
        await _factory.WithDbAsync(async db =>
        {
            db.AuthUsers.Add(new AuthUserEntity { Id = id, DisplayName = "journal-test", CreatedAt = createdAt });
            await db.SaveChangesAsync();
        });
        Assert.True(await UserExistsAsync(id));

        var first = await ReconcileAsync();
        Assert.False(await UserExistsAsync(id));
        Assert.Equal(1, first.AccountsDeleted);

        var second = await ReconcileAsync();
        Assert.Equal(0, second.Applied);
    }

    [Fact]
    public async Task AccountThatReusesTheIdWithAnotherCreationTime_IsLeftAlone()
    {
        var (id, createdAt) = await AddUserAsync();
        await InScope(sp => sp.GetRequiredService<IAccountDeletionService>().DeleteAsync(id, id, default));
        await _factory.WithDbAsync(async db =>
        {
            db.AuthUsers.Add(new AuthUserEntity { Id = id, DisplayName = "somebody-else", CreatedAt = createdAt.AddDays(9) });
            await db.SaveChangesAsync();
        });

        var result = await ReconcileAsync();

        Assert.Equal(0, result.AccountsDeleted);
        Assert.True(await UserExistsAsync(id));
    }

    [Fact]
    public async Task TelemetryWithdrawalAfterTheBackup_IsAppliedAgain_WithItsLogRow()
    {
        var granted = DateTime.UtcNow.AddDays(-10);
        var (id, createdAt) = await AddUserAsync(s =>
        {
            s.TelemetryConsent = true;
            s.TelemetryConsentAt = granted;
            s.TelemetryConsentVersion = TelemetryConsentRecord.CurrentVersion;
        });
        var withdrawnAt = DateTime.UtcNow.AddDays(-2);
        Journal.Record(new ErasureEntry(ErasureKind.TelemetryWithdrawn, id, createdAt, withdrawnAt));

        var result = await ReconcileAsync();

        Assert.Equal(1, result.TelemetryWithdrawn);
        var (consent, at, logged) = await _factory.WithDbAsync(async db => (
            (await db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == id)).TelemetryConsent,
            (await db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == id)).TelemetryConsentAt,
            await db.TelemetryConsentLog.IgnoreQueryFilters().CountAsync(l => l.AuthUserId == id && l.Answer == false)));
        Assert.False(consent);
        Assert.Equal(withdrawnAt, at);
        Assert.Equal(1, logged);
        Assert.Equal(0, (await ReconcileAsync()).Applied);
    }

    [Fact]
    public async Task ConsentGrantedAfterTheWithdrawal_IsNotRevoked()
    {
        var (id, createdAt) = await AddUserAsync(s =>
        {
            s.TelemetryConsent = true;
            s.TelemetryConsentAt = DateTime.UtcNow.AddDays(-1);
            s.TelemetryConsentVersion = TelemetryConsentRecord.CurrentVersion;
            s.AiConsentGrantedAt = DateTime.UtcNow.AddDays(-1);
            s.AiConsentProviderFingerprint = new string('a', 64);
        });
        Journal.Record(new ErasureEntry(ErasureKind.TelemetryWithdrawn, id, createdAt, DateTime.UtcNow.AddDays(-5)));
        Journal.Record(new ErasureEntry(ErasureKind.AiConsentWithdrawn, id, createdAt, DateTime.UtcNow.AddDays(-5)));

        var result = await ReconcileAsync();

        Assert.Equal(0, result.Applied);
        var row = await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == id));
        Assert.True(row.TelemetryConsent);
        Assert.NotNull(row.AiConsentGrantedAt);
    }

    [Fact]
    public async Task AiConsentWithdrawalAfterTheBackup_IsAppliedAgain()
    {
        var (id, createdAt) = await AddUserAsync(s =>
        {
            s.AiConsentGrantedAt = DateTime.UtcNow.AddDays(-10);
            s.AiConsentProviderFingerprint = new string('b', 64);
        });
        Journal.Record(new ErasureEntry(ErasureKind.AiConsentWithdrawn, id, createdAt, DateTime.UtcNow.AddDays(-2)));

        var result = await ReconcileAsync();

        Assert.Equal(1, result.AiConsentWithdrawn);
        var row = await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == id));
        Assert.Null(row.AiConsentGrantedAt);
        Assert.Null(row.AiConsentProviderFingerprint);
    }

    [Fact]
    public async Task WithdrawingTelemetry_IsJournaled_ButAFirstDeclineIsNot()
    {
        // User 1 (the seeded owner) answers through the service, the way the endpoint does.
        await InScope(async sp =>
        {
            var service = sp.GetRequiredService<ITelemetryConsentService>();
            await service.SetAsync(false);
            return 0;
        });
        Assert.DoesNotContain(Journal.Read(), e => e.Kind == ErasureKind.TelemetryWithdrawn && e.UserId == 1);

        await InScope(async sp =>
        {
            var service = sp.GetRequiredService<ITelemetryConsentService>();
            await service.SetAsync(true);
            await service.SetAsync(false);
            return 0;
        });
        Assert.Contains(Journal.Read(), e => e.Kind == ErasureKind.TelemetryWithdrawn && e.UserId == 1);
    }

    [Fact]
    public async Task EntriesOlderThanTheRetention_AreDroppedAndNotApplied()
    {
        var (id, createdAt) = await AddUserAsync();
        Journal.Record(new ErasureEntry(ErasureKind.AccountDeleted, id, createdAt, DateTime.UtcNow.AddDays(-91)));

        var result = await ReconcileAsync();

        Assert.True(result.Pruned >= 1);
        Assert.Equal(0, result.AccountsDeleted);
        Assert.True(await UserExistsAsync(id));
    }
}

/// <summary>JSON import keeps the user's consent state (audit 2026-11-20 S9).</summary>
public class BackupImportConsentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackupImportConsentTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ImportJson_KeepsTelemetryAndAiConsentOfTheImportingUser()
    {
        var grantedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var fingerprint = new string('c', 64);
        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.Settings.GetOrCreateAsync(db);
            settings.TelemetryConsent = true;
            settings.TelemetryConsentAt = grantedAt;
            settings.TelemetryConsentVersion = TelemetryConsentRecord.CurrentVersion;
            settings.AiConsentGrantedAt = grantedAt;
            settings.AiConsentProviderFingerprint = fingerprint;
            await db.SaveChangesAsync();
        });

        var export = await _client.GetFromJsonAsync<BackupExportDto>("/api/backup/export");
        var response = await _client.PostAsJsonAsync("/api/backup/import-json", export);
        response.EnsureSuccessStatusCode();

        var row = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync());
        Assert.True(row.TelemetryConsent);
        Assert.Equal(grantedAt, row.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, row.TelemetryConsentVersion);
        Assert.Equal(grantedAt, row.AiConsentGrantedAt);
        Assert.Equal(fingerprint, row.AiConsentProviderFingerprint);
    }

    [Fact]
    public async Task ImportJson_CannotPlantAConsent_ForAUserWhoNeverGaveOne()
    {
        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.Settings.GetOrCreateAsync(db);
            settings.TelemetryConsent = null;
            settings.TelemetryConsentAt = null;
            settings.TelemetryConsentVersion = null;
            settings.AiConsentGrantedAt = null;
            settings.AiConsentProviderFingerprint = null;
            await db.SaveChangesAsync();
        });
        var export = await _client.GetFromJsonAsync<BackupExportDto>("/api/backup/export");

        (await _client.PostAsJsonAsync("/api/backup/import-json", export)).EnsureSuccessStatusCode();

        var row = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync());
        Assert.Null(row.TelemetryConsent);
        Assert.Null(row.AiConsentGrantedAt);
    }
}
