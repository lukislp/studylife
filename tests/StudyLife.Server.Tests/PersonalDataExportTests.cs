// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The personal data export (GET /api/backup/export, GDPR Art. 15 - 2026-11-17 audit F-9): every
/// table of user data is either in the export or excluded with a reason, a fully populated account
/// fills every exported block, and no secret ever appears in the file.
/// </summary>
public class PersonalDataExportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PersonalDataExportTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryUserDataEntity_IsExportedOrExcludedWithAReason()
    {
        var unclassified = await _factory.WithDbAsync(db => Task.FromResult(PersonalDataExportCatalog.Unclassified(db.Model)));

        Assert.True(unclassified.Count == 0,
            "These entities hold user data but PersonalDataExportCatalog does not say whether the personal data export " +
            "(BackupDataService.BuildExportAsync) carries them - export them, or list them in NotExported with the reason: " +
            string.Join(", ", unclassified.Select(t => t.Name)));
    }

    [Fact]
    public void TheGuard_ActuallyFires_ForANewPerUserTable()
    {
        var options = new DbContextOptionsBuilder<ExportGuardProbeDb>().UseSqlite("Data Source=:memory:").Options;
        using var probe = new ExportGuardProbeDb(options);

        Assert.Contains(typeof(NewUserDataEntity), PersonalDataExportCatalog.Unclassified(probe.Model));
    }

    private sealed class NewUserDataEntity { public int Id { get; set; } public int AuthUserId { get; set; } }

    private sealed class ExportGuardProbeDb(DbContextOptions<ExportGuardProbeDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<NewUserDataEntity>();
    }

    [Fact]
    public async Task FullyPopulatedAccount_FillsEveryExportedBlock_AndLeaksNoSecret()
    {
        var (userId, explicitSecrets) = await SeedFullAccountAsync();
        var json = await ExportAsAsync(userId);
        var document = JsonNode.Parse(json)!;

        foreach (var (entity, path) in PersonalDataExportCatalog.Exported)
            Assert.True(IsFilled(Resolve(document, path)), $"{entity.Name} is catalogued at '{path}', but that is empty in the export.");

        // Spot checks of the account block beyond "filled".
        var account = document["account"]!;
        Assert.Equal("Export Person", (string?)account["displayName"]);
        Assert.Equal(9, account["accessKeys"]!.AsArray().Count);
        Assert.Equal(1, (int?)account["recoveryCodes"]!["remaining"]);
        Assert.Equal("Export App", (string?)account["registeredApps"]![0]!["name"]);
        Assert.NotNull(document["about"]!["complaint"]);

        // No secret: neither the values set explicitly here, nor any value the generic seeder put
        // into a secret-looking column (it writes "<PropertyName>-<guid>" into every string column).
        foreach (var secret in explicitSecrets)
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        var secretColumns = await _factory.WithDbAsync(db => Task.FromResult(SecretColumnNames(db).ToList()));
        Assert.NotEmpty(secretColumns);
        foreach (var column in secretColumns)
            Assert.DoesNotContain($"\"{column}-", json, StringComparison.Ordinal);
        // Passkey ids and public keys are bytes: check their base64 forms.
        var passkeyBytes = await _factory.WithDbAsync(db => db.PasskeyCredentials.AsNoTracking()
            .Where(p => p.AuthUserId == userId).Select(p => new { p.CredentialId, p.PublicKey }).ToListAsync());
        foreach (var p in passkeyBytes)
        {
            Assert.DoesNotContain(Convert.ToBase64String(p.CredentialId), json, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(p.PublicKey), json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TelemetryAndAiConsent_AreInTheAccountBlock()
    {
        var (userId, _) = await SeedFullAccountAsync();
        var answeredAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == userId);
            settings.TelemetryConsent = false;
            settings.TelemetryConsentAt = answeredAt;
            settings.TelemetryConsentVersion = TelemetryConsentRecord.CurrentVersion;
            settings.AiConsentGrantedAt = answeredAt;
            settings.AiConsentProviderFingerprint = new string('a', 64);
            // The seeded account already has generated log rows; the answer history is exactly
            // these two, oldest first.
            await db.TelemetryConsentLog.IgnoreQueryFilters().Where(l => l.AuthUserId == userId).ExecuteDeleteAsync();
            db.TelemetryConsentLog.Add(new TelemetryConsentLogEntity { AuthUserId = userId, Answer = false, AnsweredAt = answeredAt.AddDays(1), TextVersion = 2 });
            db.TelemetryConsentLog.Add(new TelemetryConsentLogEntity { AuthUserId = userId, Answer = true, AnsweredAt = answeredAt, TextVersion = 1 });
            await db.SaveChangesAsync();
        });

        var export = JsonSerializer.Deserialize<PersonalDataExportDto>(await ExportAsAsync(userId), JsonSerializerOptions.Web)!;

        Assert.False(export.Account.TelemetryConsent.Value);
        Assert.Equal(answeredAt, export.Account.TelemetryConsent.AnsweredAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, export.Account.TelemetryConsent.TextVersion);
        Assert.Collection(export.Account.TelemetryConsent.History,
            first => { Assert.True(first.Answer); Assert.Equal(answeredAt, first.AnsweredAt); Assert.Equal(1, first.TextVersion); },
            second => { Assert.False(second.Answer); Assert.Equal(answeredAt.AddDays(1), second.AnsweredAt); Assert.Equal(2, second.TextVersion); });
        Assert.Equal(answeredAt, export.Account.AiConsent.GrantedAt);
        Assert.Equal(new string('a', 64), export.Account.AiConsent.ProviderFingerprint);
    }

    [Fact]
    public async Task TheExport_ShowsOnlyTheCallersAccount()
    {
        var (alex, _) = await SeedFullAccountAsync();
        var (blake, _) = await SeedFullAccountAsync("Blake");

        var export = JsonSerializer.Deserialize<PersonalDataExportDto>(await ExportAsAsync(alex), JsonSerializerOptions.Web)!;

        Assert.Equal("Export Person", export.Account.DisplayName);
        Assert.Single(export.Account.Passkeys);
        Assert.Single(export.Account.InvitesCreated);
        Assert.Single(export.Account.RegisteredApps);
        // Two sessions: the seeded one and the one this request runs on.
        Assert.Equal(2, export.Account.SignInSessions.Count);
        Assert.NotEqual(alex, blake);
    }

    private static readonly Regex SecretName = new("Hash|Token|Secret|Plaintext|Endpoint|P256dh|PublicKey|CredentialId|^Auth$",
        RegexOptions.CultureInvariant);

    /// <summary>String columns of user-data tables whose name says they hold a secret.</summary>
    private static IEnumerable<string> SecretColumnNames(StudyLifeDb db) =>
        PersonalDataExportCatalog.UserDataTypes(db.Model)
            .Select(t => db.Model.FindEntityType(t)!)
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ClrType == typeof(string) && SecretName.IsMatch(p.Name))
            .Select(p => p.Name)
            .Distinct();

    /// <summary>One row in every per-user table (UserDataSeeder) plus the rows that need an
    /// explicit reference: all access keys, an invite, a registered app, an outbox row with a
    /// plaintext key. Returns the explicitly planted secrets.</summary>
    private async Task<(int UserId, List<string> Secrets)> SeedFullAccountAsync(string displayName = "Export Person")
    {
        var secrets = new List<string>();
        string Secret(string what)
        {
            var value = $"secret-{what}-{Guid.NewGuid():N}";
            secrets.Add(value);
            return value;
        }

        var userId = await _factory.WithDbAsync(async db =>
        {
            var id = await UserDataSeeder.SeedUserAsync(db, displayName);
            var user = await db.AuthUsers.SingleAsync(u => u.Id == id);
            var now = DateTime.UtcNow;
            user.ApiKeyHash = Secret("ha"); user.ApiKeyCreatedAt = now;
            user.AiApiKeyHash = Secret("ai"); user.AiApiKeyCreatedAt = now;
            user.McpApiKeyHash = Secret("mcp"); user.McpApiKeyCreatedAt = now;
            user.CaptureApiKeyHash = Secret("capture"); user.CaptureApiKeyCreatedAt = now;
            user.FocusGuardApiKeyHash = Secret("focusguard"); user.FocusGuardApiKeyCreatedAt = now;
            user.FocusTunesApiKeyHash = Secret("focustunes"); user.FocusTunesApiKeyCreatedAt = now;
            user.TrayApiKeyHash = Secret("tray"); user.TrayApiKeyCreatedAt = now;
            user.DeveloperApiKeyHash = Secret("developer"); user.DeveloperApiKeyCreatedAt = now;
            user.CalendarTokenHash = Secret("calendar"); user.CalendarTokenCreatedAt = now;
            user.LegacyCalendarToken = Secret("legacy-calendar");

            db.AuthInvites.Add(new AuthInviteEntity
            {
                TokenHash = Secret("invite"),
                CreatedByUserId = id,
                CreatedAt = now,
                ExpiresAt = now.AddDays(7),
            });
            db.OAuthClients.Add(new OAuthClientEntity
            {
                ClientId = $"export-app-{Guid.NewGuid():N}",
                Name = "Export App",
                OwnerAuthUserId = id,
                CreatedAt = now,
                AllowedRedirectUris = "https://app.example/callback",
                RequestedScopes = "Sessions.GetAll",
            });
            db.AiKeyOutbox.Add(new AiKeyOutboxEntity
            {
                AuthUserId = id,
                Action = AiKeyOutboxEntity.ActionRegister,
                AiApiKeyPlaintext = Secret("outbox"),
                CreatedAt = now,
            });
            var settings = await db.Settings.IgnoreQueryFilters().SingleAsync(s => s.AuthUserId == id);
            settings.ProgressShareTokenHash = Secret("share");
            settings.LegacyProgressShareToken = Secret("legacy-share");
            await db.SaveChangesAsync();
            return id;
        });
        return (userId, secrets);
    }

    /// <summary>GET /api/backup/export through the real pipeline, signed in as <paramref name="userId"/>.</summary>
    private async Task<string> ExportAsAsync(int userId)
    {
        var token = await _factory.WithDbAsync(async db =>
        {
            var issued = AuthSessionService.IssueSession(db, userId, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return issued;
        });
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);

        var response = await client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        return json;
    }

    private static JsonNode? Resolve(JsonNode root, string path) =>
        path.Split('.').Aggregate<string, JsonNode?>(root, (node, name) => node?[name]);

    private static bool IsFilled(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
        _ => true,
    };
}

/// <summary>
/// The import ignores the read-only blocks: posting the export file unchanged (with "account" and
/// "about") imports the study content and changes nothing about the account. Own class: the
/// import replaces the seeded user's data.
/// </summary>
public class PersonalDataExportImportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonalDataExportImportTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ImportingTheFullExportFile_IgnoresTheAccountAndAboutBlocks()
    {
        var note = await _client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "Kept", Content = "c" });
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);

        var exportJson = JsonNode.Parse(await _client.GetStringAsync("/api/backup/export"))!;
        Assert.NotNull(exportJson["account"]);
        Assert.NotNull(exportJson["about"]);
        // A hand-edited file must not be able to change the account either.
        exportJson["account"]!["displayName"] = "Tampered";
        exportJson["account"]!["isOwner"] = false;
        exportJson["account"]!["telemetryConsent"]!["value"] = true;
        exportJson["account"]!["passkeys"]!.AsArray().Add(new JsonObject { ["label"] = "planted" });

        var (nameBefore, passkeysBefore) = await _factory.WithDbAsync(async db => (
            (await db.AuthUsers.AsNoTracking().SingleAsync(u => u.Id == 1)).DisplayName,
            await db.PasskeyCredentials.CountAsync(p => p.AuthUserId == 1)));

        var response = await _client.PostAsync("/api/backup/import-json",
            new StringContent(exportJson.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BackupImportResponseDto>();
        Assert.Equal(1, result!.Imported["notes"]);
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.AsNoTracking().SingleAsync(u => u.Id == 1);
            Assert.Equal(nameBefore, user.DisplayName);
            Assert.True(user.IsOwner);
            Assert.Equal(passkeysBefore, await db.PasskeyCredentials.CountAsync(p => p.AuthUserId == 1));
            Assert.Null((await db.Settings.AsNoTracking().SingleAsync()).TelemetryConsent);
        });
    }
}

/// <summary>The "about" block names recipients from what the installation and the account
/// actually use - nothing more.</summary>
public class ExportAboutBuilderTests
{
    private sealed class FakeAiConsent(AiCapabilities capabilities) : IAiConsentService
    {
        public Task<AiCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(capabilities);
        public Task<AiConsentStatusDto> GetStatusAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceResult<AiConsentStatusDto>> GrantAsync(int userId, string fingerprint, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceResult> WithdrawAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceResult> EnsureGrantedAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> MayProcessInBackgroundAsync(int userId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ExportAboutBuilder Builder(AiCapabilities ai, bool addons)
    {
        (string, string?)[] Config(string section) => addons
            ? [($"{section}:BaseUrl", "https://addon.test"), ($"{section}:SharedSecret", "s")]
            : [];
        return new ExportAboutBuilder(
            new FakeAiConsent(ai),
            new WebhooksProxyClient(TestOptions.For<StudyLifeWebhooksOptions>(Config("StudyLifeWebhooks")),
                NullLogger<WebhooksProxyClient>.Instance, new HttpClient()),
            new DeveloperProxyClient(TestOptions.For<StudyLifeDevelopersOptions>(Config("StudyLifeDevelopers")),
                NullLogger<DeveloperProxyClient>.Instance, new HttpClient()));
    }

    [Fact]
    public async Task NothingConfiguredNorUsed_NamesNoRecipient_AndNoAiPurpose()
    {
        var about = await Builder(new AiCapabilities(false, null), addons: false).BuildAsync(new AccountExportDto());

        Assert.Empty(about.Recipients);
        Assert.DoesNotContain(about.Purposes, p => p.Contains("AI", StringComparison.Ordinal));
        Assert.All(new[] { about.Categories, about.Complaint, about.Source, about.AutomatedDecisions }, Assert.NotEmpty);
        Assert.NotEmpty(about.Retention);
        Assert.NotEmpty(about.Rights);
    }

    [Fact]
    public async Task UsedServices_AreNamed_IncludingEveryExternalAiProvider()
    {
        var provider = new AiProviderInfoDto
        {
            AnyExternal = true,
            Roles =
            [
                new AiProviderRoleDto { Role = "chat", Provider = "ExternalCo", Model = "big-model", Local = false },
                new AiProviderRoleDto { Role = "embeddings", Provider = "Local", Model = "small-model", Local = true },
            ],
        };
        var account = new AccountExportDto
        {
            PushDevices =
            [
                new PushDeviceExportDto { Channel = PushSubscriptionEntity.ChannelWebPush },
                new PushDeviceExportDto { Channel = PushSubscriptionEntity.ChannelApns },
            ],
            AccessKeys = [new AccessKeyExportDto { Purpose = "ha" }, new AccessKeyExportDto { Purpose = AccountExportBuilder.CalendarPurpose }],
            ConnectedApps = [new ConnectedAppExportDto { ClientId = "app" }],
        };

        var about = await Builder(new AiCapabilities(true, provider), addons: true).BuildAsync(account);
        var names = about.Recipients.Select(r => r.Name).ToList();

        Assert.Contains("ExternalCo (big-model)", names);
        Assert.DoesNotContain(names, n => n.Contains("small-model", StringComparison.Ordinal));
        Assert.Equal(9, names.Count);
        Assert.Contains(about.Purposes, p => p.Contains("AI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheExportDoesNotClaimToBeComplete_AndNamesWhatAddOnsKeepElsewhere()
    {
        var account = new AccountExportDto { ConnectedApps = [new ConnectedAppExportDto { ClientId = "app" }] };

        var about = await Builder(new AiCapabilities(true, null), addons: true).BuildAsync(account);

        Assert.DoesNotContain("Everything stored about you", about.Categories, StringComparison.Ordinal);
        Assert.Contains("add-ons", about.Categories, StringComparison.Ordinal);
        Assert.Equal(3, about.AddonData.Count);
        Assert.All(about.AddonData, line => Assert.Contains("operator", line, StringComparison.Ordinal));
        Assert.Contains(about.AddonData, l => l.StartsWith("AI add-on", StringComparison.Ordinal));
        Assert.Contains(about.AddonData, l => l.StartsWith("Webhooks add-on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAddOnNote_ForAnAddOnThatIsNeitherConfiguredNorUsed()
    {
        var about = await Builder(new AiCapabilities(false, null), addons: false).BuildAsync(new AccountExportDto());

        Assert.Empty(about.AddonData);
    }
}
