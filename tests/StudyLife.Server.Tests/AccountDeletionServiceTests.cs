// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Tests;

/// <summary>
/// The deletion use case itself (no HTTP): the data map, the policy and the add-on ordering.
/// Users are created straight in the database; user 1 is the migration-seeded owner.
/// </summary>
public class AccountDeletionServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AccountDeletionServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed class Addons
    {
        public AiKeyOutboxTestStubHandler Ai { get; }
        public AiKeyOutboxTestStubHandler Webhooks { get; }
        public AiKeyOutboxTestStubHandler Developers { get; }
        public AiProxyClient AiClient { get; }
        public WebhooksProxyClient WebhooksClient { get; }
        public DeveloperProxyClient DevelopersClient { get; }

        public Addons(HttpStatusCode status)
        {
            AiKeyOutboxTestStubHandler Handler() => new(_ => new HttpResponseMessage(status));
            Ai = Handler();
            Webhooks = Handler();
            Developers = Handler();
            AiClient = new AiProxyClient(
                TestOptions.For<StudyLifeAiOptions>(("StudyLifeAi:BaseUrl", "https://ai.test"), ("StudyLifeAi:SharedSecret", "s")),
                NullLogger<AiProxyClient>.Instance, new HttpClient(Ai));
            WebhooksClient = new WebhooksProxyClient(
                TestOptions.For<StudyLifeWebhooksOptions>(("StudyLifeWebhooks:BaseUrl", "https://hooks.test"), ("StudyLifeWebhooks:SharedSecret", "s")),
                NullLogger<WebhooksProxyClient>.Instance, new HttpClient(Webhooks));
            DevelopersClient = new DeveloperProxyClient(
                TestOptions.For<StudyLifeDevelopersOptions>(("StudyLifeDevelopers:BaseUrl", "https://dev.test"), ("StudyLifeDevelopers:SharedSecret", "s")),
                NullLogger<DeveloperProxyClient>.Instance, new HttpClient(Developers));
        }
    }

    private static AccountDeletionService Service(IServiceScope scope, Addons? addons = null) =>
        addons is null
            ? ActivatorUtilities.CreateInstance<AccountDeletionService>(scope.ServiceProvider)
            : ActivatorUtilities.CreateInstance<AccountDeletionService>(
                scope.ServiceProvider, addons.AiClient, addons.WebhooksClient, addons.DevelopersClient);

    [Fact]
    public async Task Delete_RemovesEverythingOfTheUser_AndNothingOfAnyoneElse()
    {
        int a = 0, b = 0;
        await _factory.WithDbAsync(async db =>
        {
            a = await UserDataSeeder.SeedUserAsync(db, "Alex");
            b = await UserDataSeeder.SeedUserAsync(db, "Blake");

            // The explicit tables: invites on both sides of the reference, apps and the keys issued
            // for them, and the outbox.
            db.AuthInvites.AddRange(
                new AuthInviteEntity { CreatedByUserId = a, TokenHash = "inv-by-a", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) },
                new AuthInviteEntity { CreatedByUserId = 1, TokenHash = "inv-used-by-a", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), UsedAt = DateTime.UtcNow, UsedByUserId = a },
                new AuthInviteEntity { CreatedByUserId = b, TokenHash = "inv-by-b", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            db.OAuthClients.AddRange(
                new OAuthClientEntity { ClientId = "app-of-a", Name = "A's app", OwnerAuthUserId = a, CreatedAt = DateTime.UtcNow },
                new OAuthClientEntity { ClientId = "app-of-b", Name = "B's app", OwnerAuthUserId = b, CreatedAt = DateTime.UtcNow });
            db.ClientApiKeys.AddRange(
                new ClientApiKeyEntity { AuthUserId = b, ClientId = "app-of-a", KeyHash = "key-b-for-a-app", CreatedAt = DateTime.UtcNow },
                new ClientApiKeyEntity { AuthUserId = a, ClientId = "app-of-b", KeyHash = "key-a-for-b-app", CreatedAt = DateTime.UtcNow });
            db.AiKeyOutbox.AddRange(
                new AiKeyOutboxEntity { AuthUserId = a, Action = AiKeyOutboxEntity.ActionRegister, AiApiKeyPlaintext = "plaintext-of-a", CreatedAt = DateTime.UtcNow },
                new AiKeyOutboxEntity { AuthUserId = a, Action = AiKeyOutboxEntity.ActionRevoke, CreatedAt = DateTime.UtcNow },
                new AiKeyOutboxEntity { AuthUserId = b, Action = AiKeyOutboxEntity.ActionRegister, AiApiKeyPlaintext = "plaintext-of-b", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        // What belongs to the other user, per table, before the deletion.
        var beforeOfB = await _factory.WithDbAsync(async db =>
        {
            var counts = new Dictionary<string, int>();
            foreach (var table in UserDataCatalog.PerUserTables(db.Model))
                counts[table.ClrType.Name] = await UserDataSeeder.CountAsync(db, table.ClrType, UserDataCatalog.UserColumn, b);
            return counts;
        });
        Assert.All(beforeOfB.Values, n => Assert.True(n > 0, "the seeder must put a row into every per-user table"));

        using (var scope = _factory.Services.CreateScope())
        {
            var result = await Service(scope).DeleteAsync(a, initiatedByUserId: a, CancellationToken.None);
            Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
            Assert.False(result.Value!.InstallationReset);
        }

        await _factory.WithDbAsync(async db =>
        {
            // Generic rule: not one row of the deleted user is left in any per-user table ...
            foreach (var table in UserDataCatalog.PerUserTables(db.Model))
            {
                Assert.Equal(0, await UserDataSeeder.CountAsync(db, table.ClrType, UserDataCatalog.UserColumn, a));
                // ... and the other user's rows are exactly as before - except the one key that
                // was issued for the deleted user's own app (checked explicitly below).
                var expectedOfB = table.ClrType == typeof(ClientApiKeyEntity)
                    ? beforeOfB[table.ClrType.Name] - 1
                    : beforeOfB[table.ClrType.Name];
                Assert.Equal(expectedOfB,
                    await UserDataSeeder.CountAsync(db, table.ClrType, UserDataCatalog.UserColumn, b));
            }

            Assert.False(await db.AuthUsers.AnyAsync(u => u.Id == a));
            Assert.True(await db.AuthUsers.AnyAsync(u => u.Id == b));

            // Invites: created by the user -> gone; consumed by the user -> kept, spent, unreferenced.
            Assert.False(await db.AuthInvites.AnyAsync(i => i.TokenHash == "inv-by-a"));
            Assert.True(await db.AuthInvites.AnyAsync(i => i.TokenHash == "inv-by-b"));
            var spent = await db.AuthInvites.SingleAsync(i => i.TokenHash == "inv-used-by-a");
            Assert.Null(spent.UsedByUserId);
            Assert.NotNull(spent.UsedAt);

            // Apps: the user's app and every key issued for it (to another user) are gone; the other
            // user's app stays, only the deleted user's key for it went with the user.
            Assert.False(await db.OAuthClients.AnyAsync(c => c.ClientId == "app-of-a"));
            Assert.False(await db.ClientApiKeys.AnyAsync(k => k.ClientId == "app-of-a"));
            Assert.True(await db.OAuthClients.AnyAsync(c => c.ClientId == "app-of-b"));
            Assert.False(await db.ClientApiKeys.AnyAsync(k => k.KeyHash == "key-a-for-b-app"));

            // Outbox: the plaintext key is gone, the other user's row is untouched.
            Assert.False(await db.AiKeyOutbox.AnyAsync(o => o.AuthUserId == a && o.AiApiKeyPlaintext != null));
            Assert.True(await db.AiKeyOutbox.AnyAsync(o => o.AuthUserId == b && o.AiApiKeyPlaintext == "plaintext-of-b"));
            // The pre-existing revoke row survives: it is the retry queue and holds no personal data.
            Assert.True(await db.AiKeyOutbox.AnyAsync(o => o.AuthUserId == a && o.Action == AiKeyOutboxEntity.ActionRevoke));
        });
    }

    [Fact]
    public async Task Delete_QueuesAddonCleanupsInTheSameTransaction_AndSurvivesAnAddonOutage()
    {
        int a = 0;
        await _factory.WithDbAsync(async db =>
        {
            a = await UserDataSeeder.SeedUserAsync(db, "Casey");
            await db.AuthUsers.Where(u => u.Id == a).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AiApiKeyHash, "ai-hash-of-casey")
                .SetProperty(u => u.DeveloperApiKeyHash, "dev-hash-of-casey"));
        });

        var outage = new Addons(HttpStatusCode.InternalServerError);
        AccountDeletionResult result;
        using (var scope = _factory.Services.CreateScope())
            result = await Service(scope, outage).DeleteAsync(a, a, CancellationToken.None);

        // The account is gone although every add-on refused ...
        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(3, result.Value!.AddonCleanupPending);
        Assert.False(await _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == a)));

        // ... and the three clean-ups wait in the outbox, with nothing in them but the user id.
        var rows = await _factory.WithDbAsync(db =>
            db.AiKeyOutbox.AsNoTracking().Where(o => o.AuthUserId == a).OrderBy(o => o.Action).ToListAsync());
        Assert.Equal(
            new[] { AiKeyOutboxEntity.ActionPurgeWebhooks, AiKeyOutboxEntity.ActionRevoke, AiKeyOutboxEntity.ActionRevokeDeveloper },
            rows.Select(r => r.Action).ToArray());
        Assert.All(rows, r => { Assert.Null(r.AiApiKeyPlaintext); Assert.Equal(1, r.Attempts); });

        Assert.Contains($"/internal/users/{a}", Assert.Single(outage.Webhooks.Requests).Uri);
        Assert.Contains("revoke-key", Assert.Single(outage.Ai.Requests).Uri);
        Assert.Contains("revoke-key", Assert.Single(outage.Developers.Requests).Uri);

        // The background drain delivers them once the add-ons are back, then the rows are gone.
        var back = new Addons(HttpStatusCode.OK);
        var services = new ServiceCollection()
            .AddSingleton(back.WebhooksClient).AddSingleton(back.DevelopersClient).BuildServiceProvider();
        var worker = new BackgroundTaskService(services,
            _factory.Services.GetRequiredService<VapidKeysHolder>(),
            NullLogger<BackgroundTaskService>.Instance,
            _factory.Services.GetRequiredService<ApnsSender>(),
            aiProxyClient: back.AiClient);
        await _factory.WithDbAsync(db => db.AiKeyOutbox.Where(o => o.AuthUserId == a)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastAttemptAt, DateTime.UtcNow.AddHours(-2))));
        await _factory.WithDbAsync(db => worker.RunAiKeyOutboxAsync(db));

        Assert.Empty(await _factory.WithDbAsync(db => db.AiKeyOutbox.Where(o => o.AuthUserId == a).ToListAsync()));
        Assert.Contains($"/internal/users/{a}", Assert.Single(back.Webhooks.Requests).Uri);
    }

    [Fact]
    public async Task Delete_AgainstAnOlderWebhooksWithoutTheEndpoint_StaysQueuedInsteadOfBlocking()
    {
        int a = 0;
        await _factory.WithDbAsync(async db => a = await UserDataSeeder.SeedUserAsync(db, "Drew"));

        var older = new Addons(HttpStatusCode.NotFound); // an older studylife-webhooks has no such route
        using var scope = _factory.Services.CreateScope();
        var result = await Service(scope, older).DeleteAsync(a, a, CancellationToken.None);

        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(1, result.Value!.AddonCleanupPending); // only webhooks: no AI key, no developer key
        Assert.Equal(AiKeyOutboxEntity.ActionPurgeWebhooks,
            (await _factory.WithDbAsync(db => db.AiKeyOutbox.SingleAsync(o => o.AuthUserId == a))).Action);
    }

    [Fact]
    public async Task Delete_WithHealthyAddons_LeavesNothingQueued()
    {
        int a = 0;
        await _factory.WithDbAsync(async db =>
        {
            a = await UserDataSeeder.SeedUserAsync(db, "Emery");
            await db.AuthUsers.Where(u => u.Id == a).ExecuteUpdateAsync(s => s.SetProperty(u => u.AiApiKeyHash, "x"));
        });

        var healthy = new Addons(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var result = await Service(scope, healthy).DeleteAsync(a, a, CancellationToken.None);

        Assert.Equal(0, result.Value!.AddonCleanupPending);
        Assert.Empty(await _factory.WithDbAsync(db => db.AiKeyOutbox.Where(o => o.AuthUserId == a).ToListAsync()));
        Assert.Single(healthy.Ai.Requests);
        Assert.Single(healthy.Webhooks.Requests);
        Assert.Empty(healthy.Developers.Requests); // no developer key, nothing to revoke
    }

    [Fact]
    public async Task Policy_OwnerCannotLeaveWhileOthersExist_AndNothingIsTouched()
    {
        int other = 0;
        await _factory.WithDbAsync(async db => other = await UserDataSeeder.SeedUserAsync(db, "Frankie"));
        var notesBefore = await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync());

        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);
        Assert.Equal(AccountDeletionOutcome.OwnerHasOtherUsers, await service.CheckPolicyAsync(1, 1));
        var result = await service.DeleteAsync(1, 1, CancellationToken.None);

        Assert.Equal(AccountDeletionOutcome.OwnerHasOtherUsers, result.Outcome);
        Assert.True(await _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == 1)));
        Assert.Equal(notesBefore, await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync()));

        var preview = await service.PreviewAsync(1);
        Assert.False(preview.Available);
        Assert.Equal("owner-has-other-users", preview.UnavailableReason);
        Assert.True(preview.IsOwner);
        Assert.False(preview.InstallationWillReset);
        Assert.True((await service.PreviewAsync(other)).Available);
    }

    [Fact]
    public async Task Policy_OnlyTheOwnerMayRemoveSomeoneElse_AndTheOwnerRemovesThroughTheSameUseCase()
    {
        int guest = 0, bystander = 0;
        await _factory.WithDbAsync(async db =>
        {
            guest = await UserDataSeeder.SeedUserAsync(db, "Gray");
            bystander = await UserDataSeeder.SeedUserAsync(db, "Harper");
        });

        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);
        Assert.Equal(AccountDeletionOutcome.InitiatorNotOwner, (await service.DeleteAsync(guest, bystander, CancellationToken.None)).Outcome);
        Assert.True(await _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == guest)));

        Assert.Equal(AccountDeletionOutcome.NotFound, (await service.DeleteAsync(987654, 1, CancellationToken.None)).Outcome);

        // A non-owner learns nothing about which ids exist: existing and unknown ids answer alike.
        Assert.Equal(AccountDeletionOutcome.InitiatorNotOwner, (await service.DeleteAsync(bystander, guest, CancellationToken.None)).Outcome);
        Assert.Equal(AccountDeletionOutcome.InitiatorNotOwner, (await service.DeleteAsync(987654, guest, CancellationToken.None)).Outcome);
        Assert.Equal(AccountDeletionOutcome.InitiatorNotOwner, await service.CheckPolicyAsync(987654, bystander));

        Assert.Equal(AccountDeletionOutcome.Deleted, (await service.DeleteAsync(guest, 1, CancellationToken.None)).Outcome);
        Assert.False(await _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == guest)));

        Assert.Contains(await service.ListOtherUsersAsync(1), u => u.Id == bystander);
        Assert.DoesNotContain(await service.ListOtherUsersAsync(1), u => u.Id == guest || u.Id == 1);
    }

    [Fact]
    public async Task Delete_OnADemoInstance_IsRefusedByTheServiceItself()
    {
        int a = 0;
        await _factory.WithDbAsync(async db => a = await UserDataSeeder.SeedUserAsync(db, "Indigo"));

        var demoConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DEMO_MODE"] = "true",
            ["DEMO_MODE_CONFIRM_DATA_LOSS"] = "yes-delete-all-data",
        }).Build();
        using var scope = _factory.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<AccountDeletionService>(scope.ServiceProvider, (IConfiguration)demoConfig);

        Assert.Equal(AccountDeletionOutcome.DemoMode, (await service.DeleteAsync(a, a, CancellationToken.None)).Outcome);
        Assert.Equal("demo", (await service.PreviewAsync(a)).UnavailableReason);
        Assert.True(await _factory.WithDbAsync(db => db.AuthUsers.AnyAsync(u => u.Id == a)));
    }
}
