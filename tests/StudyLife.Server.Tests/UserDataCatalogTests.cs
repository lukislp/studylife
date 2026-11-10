// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Tests;

/// <summary>
/// The guard that keeps account deletion complete in the future. If a new entity with a user
/// reference is added to the model and nobody decides how deletion treats it, one of these fails -
/// before a user's data is left behind in production. See <see cref="UserDataCatalog"/> for the
/// decisions each failure asks for.
/// </summary>
public class UserDataCatalogTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public UserDataCatalogTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryEntityOfTheModel_IsClassifiedForDeletion()
    {
        var unclassified = await _factory.WithDbAsync(db => Task.FromResult(UserDataCatalog.Unclassified(db.Model)));

        Assert.True(unclassified.Count == 0,
            "These entities are neither per-user (AuthUserId), nor handled explicitly, nor declared instance-wide in " +
            "UserDataCatalog - decide what account deletion does with them: " + string.Join(", ", unclassified.Select(t => t.Name)));
    }

    [Fact]
    public async Task EveryUserReferenceColumn_HasADeletionDecision()
    {
        var undecided = await _factory.WithDbAsync(db => Task.FromResult(UserDataCatalog.UndecidedUserColumns(db.Model)));

        Assert.True(undecided.Count == 0,
            "These columns reference a user but account deletion does not know about them - handle them in " +
            "AccountDeletionService and list them in UserDataCatalog.ExplicitColumns: " +
            string.Join(", ", undecided.Select(c => $"{c.Entity.Name}.{c.Property}")));
    }

    [Fact]
    public async Task TheGenericRule_CoversTheTablesTheBriefNames()
    {
        var names = await _factory.WithDbAsync(db =>
            Task.FromResult(UserDataCatalog.PerUserTables(db.Model).Select(t => t.ClrType.Name).ToHashSet()));

        foreach (var expected in new[]
        {
            nameof(StudySessionEntity), nameof(UserSettingsEntity), nameof(PushSubscriptionEntity), nameof(SentReminderEntity),
            nameof(NoteEntity), nameof(CourseGoalEntity), nameof(TimerStateEntity), nameof(StudyProgramEntity),
            nameof(CourseGroupEntity), nameof(CustomCourseEntity), nameof(SessionTemplateEntity), nameof(CourseResourceEntity),
            nameof(PasskeyCredentialEntity), nameof(AuthSessionEntity), nameof(RecoveryCodeEntity),
            nameof(WebhookApiKeyEntity), nameof(ClientApiKeyEntity),
        })
            Assert.Contains(expected, names);

        // The outbox keeps its revoke rows on purpose, so it must NOT be swept by the generic rule.
        Assert.DoesNotContain(nameof(AiKeyOutboxEntity), names);
    }

    [Fact]
    public void TheGuard_ActuallyFires_ForAnEntityNobodyDecidedAbout()
    {
        // A throwaway model with one entity that has no user column and one with an undecided one.
        var options = new DbContextOptionsBuilder<GuardProbeDb>().UseSqlite("Data Source=:memory:").Options;
        using var probe = new GuardProbeDb(options);

        Assert.Contains(typeof(StrayEntity), UserDataCatalog.Unclassified(probe.Model));
        Assert.Contains((typeof(UserRefEntity), "ShareOwnerUserId"), UserDataCatalog.UndecidedUserColumns(probe.Model));
    }

    private sealed class StrayEntity { public int Id { get; set; } }
    private sealed class UserRefEntity { public int Id { get; set; } public int ShareOwnerUserId { get; set; } }

    private sealed class GuardProbeDb(DbContextOptions<GuardProbeDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StrayEntity>();
            modelBuilder.Entity<UserRefEntity>();
        }
    }
}

/// <summary>
/// Seeds one row of EVERY per-user table for a user, derived from the model the same way deletion
/// derives its list - so a table added later is seeded, and therefore verified, automatically.
/// </summary>
internal static class UserDataSeeder
{
    public static async Task<int> SeedUserAsync(StudyLifeDb db, string displayName, bool owner = false)
    {
        var user = new AuthUserEntity { DisplayName = displayName, CreatedAt = DateTime.UtcNow, IsOwner = owner };
        db.AuthUsers.Add(user);
        await db.SaveChangesAsync();

        foreach (var type in UserDataCatalog.PerUserTables(db.Model))
            db.Add(Create(type, user.Id));
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static object Create(IEntityType type, int userId)
    {
        var entity = Activator.CreateInstance(type.ClrType)!;
        foreach (var property in type.GetProperties())
        {
            var info = property.PropertyInfo;
            if (property.IsPrimaryKey() || info is null || !info.CanWrite) continue;
            if (property.Name == UserDataCatalog.UserColumn) info.SetValue(entity, userId);
            else if (property.ClrType == typeof(string)) info.SetValue(entity, $"{property.Name}-{Guid.NewGuid():N}");
            else if (property.ClrType == typeof(byte[])) info.SetValue(entity, RandomNumberGenerator.GetBytes(16));
        }
        return entity;
    }

    private static readonly MethodInfo CountMethod =
        typeof(UserDataSeeder).GetMethod(nameof(CountOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Rows of one entity type whose given column equals the user id.</summary>
    public static Task<int> CountAsync(StudyLifeDb db, Type entityType, string column, int userId) =>
        (Task<int>)CountMethod.MakeGenericMethod(entityType).Invoke(null, [db, column, userId])!;

    private static Task<int> CountOfAsync<T>(StudyLifeDb db, string column, int userId) where T : class =>
        db.Set<T>().IgnoreQueryFilters().CountAsync(e => EF.Property<int>(e, column) == userId);
}
