using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

// The operation services extracted from SettingsController/PushController/BackupController/
// SystemController/DeveloperController, exercised directly (no HTTP) via the same
// WithServiceAsync helper as DomainServiceTests. The controller-level suites
// (SettingsControllerTests, PushControllerTests, BackupImportTests, ...) still pin the wire
// behavior; these pin the domain behavior.

public class SettingsServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SettingsServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static UserSettingsDto ValidSettings() => new()
    {
        StudyWindowStartHour = 8,
        StudyWindowEndHour = 21,
        WeeklyGoalMinHours = 25,
        WeeklyGoalMaxHours = 30,
        MonthlyGoalMinHours = 100,
        MonthlyGoalMaxHours = 130,
        SelectedCourseIds = new List<int>(),
        CompletedCourseIds = new List<int>(),
    };

    [Fact]
    public async Task SaveAsync_ValidSettings_PersistsThemAndIncrementsTheVersion()
    {
        var dto = ValidSettings();
        dto.Theme = "dark";

        var first = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(dto));

        Assert.Equal(ServiceOutcome.Success, first.Outcome);
        Assert.Equal("dark", first.Value!.Theme);

        var reloaded = await _factory.WithServiceAsync<ISettingsService, UserSettingsDto>(
            (settings, _) => settings.LoadAsync());
        Assert.Equal("dark", reloaded.Theme);
        Assert.Equal(first.Value.Version, reloaded.Version);
    }

    [Fact]
    public async Task SaveAsync_StaleVersion_ConflictsAndHandsBackTheCurrentRow()
    {
        var dto = ValidSettings();
        dto.AccentColor = "coral";
        var current = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(dto));

        var stale = ValidSettings();
        stale.AccentColor = "mint";
        stale.Version = current.Value!.Version - 1;

        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(stale));

        Assert.Equal(ServiceOutcome.Conflict, result.Outcome);
        // The 409 body is the CURRENT state, not the rejected input - that is what lets the
        // client rebase instead of guessing.
        Assert.Equal("coral", result.Value!.AccentColor);
        Assert.Equal(current.Value.Version, result.Value.Version);
    }

    [Fact]
    public async Task SaveAsync_NullVersion_IsAcceptedWithoutAnyPrecondition()
    {
        var dto = ValidSettings();
        dto.Version = null;

        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(dto));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task SaveAsync_InvertedStudyWindow_IsInvalid()
    {
        var dto = ValidSettings();
        dto.StudyWindowStartHour = 20;
        dto.StudyWindowEndHour = 9;

        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(dto));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("StudyWindowStartHour/StudyWindowEndHour must be 0-23, end after start.", result.Error);
    }

    [Fact]
    public async Task SaveAsync_UnknownActiveStudyProgramId_IsInvalid()
    {
        var dto = ValidSettings();
        dto.ActiveStudyProgramId = 999999;

        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.SaveAsync(dto));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("ActiveStudyProgramId does not reference an existing study program.", result.Error);
    }
}

public class SettingsServiceProgressShareTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SettingsServiceProgressShareTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task EnableThenRegenerateThenDisable_RotatesTheTokenAndFinallyClearsIt()
    {
        var enabled = await _factory.WithServiceAsync<ISettingsService, UserSettingsDto>(
            (settings, _) => settings.EnableProgressShareAsync());
        Assert.True(enabled.ProgressShareEnabled);
        Assert.False(string.IsNullOrWhiteSpace(enabled.ProgressShareToken));

        var regenerated = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.RegenerateProgressShareTokenAsync());
        Assert.Equal(ServiceOutcome.Success, regenerated.Outcome);
        Assert.NotEqual(enabled.ProgressShareToken, regenerated.Value!.ProgressShareToken);
        Assert.True(regenerated.Value.ProgressShareEnabled);

        var disabled = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.DisableProgressShareAsync());
        Assert.False(disabled.Value!.ProgressShareEnabled);
        // Disabling deletes the token too, so a leaked link can't come back by re-enabling.
        Assert.Null(disabled.Value.ProgressShareToken);
    }
}

public class SettingsServiceDismissBuiltInProgramTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SettingsServiceDismissBuiltInProgramTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DismissBuiltInProgramAsync_WithoutAnOwnProgram_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.DismissBuiltInProgramAsync());

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Create your own study program before hiding the default one.", result.Error);
    }

    [Fact]
    public async Task DismissBuiltInProgramAsync_WithAnOwnProgram_ActivatesItAndHidesTheBuiltIn()
    {
        var created = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramSummaryDto>>(
            (programs, _) => programs.CreateAsync(new CreateStudyProgramRequestDto
            {
                Name = "Eigener Studiengang",
                Courses = new List<CreateStudyProgramCourseDto>
                {
                    new() { Name = "Kurs", Semester = 1, Ects = 5, Topics = new List<string>() },
                },
            }));

        var result = await _factory.WithServiceAsync<ISettingsService, ServiceResult<UserSettingsDto>>(
            (settings, _) => settings.DismissBuiltInProgramAsync());

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.True(result.Value!.BuiltInProgramDismissed);
        Assert.Equal(created.Value!.Id, result.Value.ActiveStudyProgramId);

        var summaries = await _factory.WithServiceAsync<IStudyProgramService, List<StudyProgramSummaryDto>>(
            (programs, _) => programs.GetSummariesAsync());
        Assert.DoesNotContain(summaries, s => s.IsBuiltIn);
    }
}

public class ApiKeyServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    // AuthUserId 1 is the user the AddMultiTenantAuthUserFoundation migration seeds into every
    // freshly migrated temp DB - the same one CustomWebApplicationFactory logs its clients in as.
    private const int SeededUserId = 1;

    public ApiKeyServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GenerateAsync_ThenRevokeAsync_FlipsOnlyThatSlotsStatus()
    {
        var before = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<HaApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToHaApiKeyStatusDto));
        Assert.False(before.Value!.HasKey);

        var generated = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<HaApiKeyGenerateResponseDto>>(
            (keys, _) => keys.GenerateAsync(SeededUserId, ApiKeySlots.Ha,
                (key, createdAt) => new HaApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt }));
        Assert.Equal(ServiceOutcome.Success, generated.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(generated.Value!.ApiKey));

        var afterGenerate = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<HaApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToHaApiKeyStatusDto));
        Assert.True(afterGenerate.Value!.HasKey);
        // A different slot is untouched by the one that was just written.
        var otherSlot = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<McpApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToMcpApiKeyStatusDto));
        Assert.False(otherSlot.Value!.HasKey);

        var revoked = await _factory.WithServiceAsync<IApiKeyService, ServiceResult>(
            (keys, _) => keys.RevokeAsync(SeededUserId, ApiKeySlots.Ha));
        Assert.Equal(ServiceOutcome.Success, revoked.Outcome);

        var afterRevoke = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<HaApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToHaApiKeyStatusDto));
        Assert.False(afterRevoke.Value!.HasKey);
        // Revoke clears the timestamp too - a revoked slot can never report "no key, created at ...".
        Assert.Null(afterRevoke.Value.CreatedAt);
    }

    [Fact]
    public async Task GetStatusAsync_UnknownUser_IsUnauthorized()
    {
        var result = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<HaApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(999999, ApiKeyService.ToHaApiKeyStatusDto));

        Assert.Equal(ServiceOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task GenerateAiKeyAsync_EnqueuesTheOutboxRowAndConsumesItOnTheFastPath()
    {
        // studylife-ai is unconfigured in tests, so AiProxyClient reports "delivered" (there is
        // nothing to deliver to, see PostInternalAsync) and the fast path removes the row again
        // - the audit-A7 outbox row exists during the attempt, not afterwards.
        var result = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<AiApiKeyGenerateResponseDto>>(
            (keys, _) => keys.GenerateAiKeyAsync(SeededUserId, CancellationToken.None));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.ApiKey));
        var status = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<AiApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToAiApiKeyStatusDto));
        Assert.True(status.Value!.HasKey);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.AiKeyOutbox.AsNoTracking()
            .CountAsync(r => r.Action == AiKeyOutboxEntity.ActionRegister)));
    }

    [Fact]
    public async Task RevokeAiKeyAsync_ClearsTheSlotAndConsumesItsOutboxRow()
    {
        await _factory.WithServiceAsync<IApiKeyService>((keys, _) => keys.GenerateAiKeyAsync(SeededUserId, CancellationToken.None));

        var result = await _factory.WithServiceAsync<IApiKeyService, ServiceResult>(
            (keys, _) => keys.RevokeAiKeyAsync(SeededUserId, CancellationToken.None));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        var status = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<AiApiKeyStatusDto>>(
            (keys, _) => keys.GetStatusAsync(SeededUserId, ApiKeyService.ToAiApiKeyStatusDto));
        Assert.False(status.Value!.HasKey);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.AiKeyOutbox.AsNoTracking().CountAsync()));
    }
}

public class ApiKeyServiceWebhookKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public ApiKeyServiceWebhookKeyTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateWebhookKeyAsync_EmptyName_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<CreateWebhookApiKeyResponseDto>>(
            (keys, _) => keys.CreateWebhookKeyAsync(SeededUserId, new CreateWebhookApiKeyRequestDto { Name = "   " }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Name must not be empty.", result.Error);
    }

    [Fact]
    public async Task CreateThenDeleteWebhookKeyAsync_RoundTripsAndNeverLeaksThePlaintextAgain()
    {
        var created = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<CreateWebhookApiKeyResponseDto>>(
            (keys, _) => keys.CreateWebhookKeyAsync(SeededUserId, new CreateWebhookApiKeyRequestDto { Name = "Mein Add-on" }));
        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(created.Value!.ApiKey));

        var listed = await _factory.WithServiceAsync<IApiKeyService, List<WebhookApiKeyDto>>(
            (keys, _) => keys.GetWebhookKeysAsync(SeededUserId));
        var row = Assert.Single(listed, k => k.Id == created.Value.Id);
        Assert.Equal("Mein Add-on", row.Name);

        var deleted = await _factory.WithServiceAsync<IApiKeyService, ServiceResult>(
            (keys, _) => keys.DeleteWebhookKeyAsync(SeededUserId, created.Value.Id));
        Assert.Equal(ServiceOutcome.Success, deleted.Outcome);
        Assert.Equal(ServiceOutcome.NotFound,
            (await _factory.WithServiceAsync<IApiKeyService, ServiceResult>(
                (keys, _) => keys.DeleteWebhookKeyAsync(SeededUserId, created.Value.Id))).Outcome);
    }

    [Fact]
    public async Task DeleteWebhookKeyAsync_AnotherUsersKey_IsNotFound()
    {
        var created = await _factory.WithServiceAsync<IApiKeyService, ServiceResult<CreateWebhookApiKeyResponseDto>>(
            (keys, _) => keys.CreateWebhookKeyAsync(SeededUserId, new CreateWebhookApiKeyRequestDto { Name = "Fremd" }));

        // WebhookApiKeys has no EF query filter (see StudyLifeDb) - the explicit AuthUserId
        // clause in the service is the only thing keeping one user out of another's rows.
        var result = await _factory.WithServiceAsync<IApiKeyService, ServiceResult>(
            (keys, _) => keys.DeleteWebhookKeyAsync(SeededUserId + 1, created.Value!.Id));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }
}

public class PushServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PushServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SubscribeAsync_LoopbackEndpoint_IsRejectedByTheOutboundUrlPolicy()
    {
        var result = await _factory.WithServiceAsync<IPushService, ServiceResult>(
            (push, _) => push.SubscribeAsync(new PushSubscribeRequest("http://127.0.0.1:9/push", "p", "a"), null));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Endpoint must be a public https URL.", result.Error);
    }

    [Fact]
    public async Task SubscribeAsync_Twice_UpdatesTheSameRowAndKeepsCreatedAt()
    {
        const string endpoint = "https://push.example.com/abc";
        await _factory.WithServiceAsync<IPushService>(
            (push, _) => push.SubscribeAsync(new PushSubscribeRequest(endpoint, "p1", "a1"), "Firefox"));
        var first = await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().FirstAsync(s => s.Endpoint == endpoint));

        await _factory.WithServiceAsync<IPushService>(
            (push, _) => push.SubscribeAsync(new PushSubscribeRequest(endpoint, "p2", "a2"), "Firefox 2"));

        var rows = await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == endpoint).ToListAsync());
        var only = Assert.Single(rows);
        Assert.Equal("p2", only.P256dh);
        Assert.Equal("Firefox 2", only.UserAgent);
        Assert.Equal(first.CreatedAt, only.CreatedAt); // "registered X days ago" must not reset
    }

    [Fact]
    public void IsValidApnsToken_RejectsAnythingOutsideTheUrlPathSafeAlphabet()
    {
        Assert.True(PushService.IsValidApnsToken("abcDEF012_-"));
        Assert.False(PushService.IsValidApnsToken(null));
        Assert.False(PushService.IsValidApnsToken("   "));
        Assert.False(PushService.IsValidApnsToken("short"));
        Assert.False(PushService.IsValidApnsToken("has/slash/in/it"));
        Assert.False(PushService.IsValidApnsToken("has.dot.in.it"));
    }

    [Fact]
    public async Task SubscribeApnsThenUnsubscribeApnsAsync_RoundTripsOnTheSyntheticEndpoint()
    {
        const string token = "aabbccddeeff0011";
        await _factory.WithServiceAsync<IPushService>(
            (push, _) => push.SubscribeApnsAsync(new ApnsSubscribeRequest(token, "Alex' iPhone")));

        var subs = await _factory.WithServiceAsync<IPushService, List<PushSubscriptionListItemDto>>(
            (push, _) => push.GetSubscriptionsAsync());
        Assert.Contains(subs, s => s.UserAgent == "Alex' iPhone");
        // The raw endpoint never leaves the server, only its one-way hash.
        Assert.All(subs, s => Assert.Equal(64, s.EndpointHash.Length));

        await _factory.WithServiceAsync<IPushService>((push, _) => push.UnsubscribeApnsAsync(token));

        var remaining = await _factory.WithDbAsync(db => db.PushSubscriptions.AsNoTracking()
            .CountAsync(s => s.Endpoint == $"apns:{token}"));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_UnknownId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<IPushService, ServiceResult>(
            (push, _) => push.DeleteSubscriptionAsync(999999));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }
}

public class BackupDataServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public BackupDataServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TouchLastBackupDownloadAsync_StampsTheSettingsRow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _factory.WithServiceAsync<IBackupDataService>((backup, _) => backup.TouchLastBackupDownloadAsync());

        var stored = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().FirstAsync());
        Assert.NotNull(stored.LastBackupDownloadAt);
        Assert.True(stored.LastBackupDownloadAt >= before);
    }

    [Fact]
    public async Task BuildExportAsync_CarriesTheV2FormatAndTheUsersRows()
    {
        await _factory.WithServiceAsync<ISessionService>((sessions, _) => sessions.CreateAsync(new StudySessionDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            CourseName = "ignored",
            CourseColor = "#000000",
            StartTime = DateTime.Now.AddDays(1),
            EndTime = DateTime.Now.AddDays(1).AddHours(1),
        }));

        var export = await _factory.WithServiceAsync<IBackupDataService, BackupExportDto>(
            (backup, _) => backup.BuildExportAsync());

        Assert.Equal(2, export.FormatVersion);
        Assert.NotEmpty(export.Sessions);
        Assert.NotNull(export.Settings);
    }

    [Fact]
    public async Task ImportJsonAsync_UnsupportedFormatVersion_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<IBackupDataService, ServiceResult<BackupImportResponseDto>>(
            (backup, _) => backup.ImportJsonAsync(new BackupExportDto { FormatVersion = 7 }, SeededUserId));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Unsupported export formatVersion 7.", result.Error);
    }
}

public class BackupDataServiceRoundTripTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public BackupDataServiceRoundTripTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ExportThenImportAsync_RemapsCustomCourseIdsOntoTheFreshlyInsertedRows()
    {
        await _factory.WithServiceAsync<IStudyProgramService>((programs, _) => programs.CreateAsync(new CreateStudyProgramRequestDto
        {
            Name = "Round Trip",
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Name = "Kurs A", Semester = 1, Ects = 5, Topics = new List<string>() },
            },
        }));
        var customCourseId = await _factory.WithDbAsync(async db =>
            StudyProgramCatalog.CustomCourseIdOffset + await db.CustomCourses.AsNoTracking().Select(c => c.Id).FirstAsync());
        await _factory.WithServiceAsync<ISessionService>((sessions, _) => sessions.CreateAsync(new StudySessionDto
        {
            CourseId = customCourseId,
            CourseName = "ignored",
            CourseColor = "#000000",
            StartTime = DateTime.Now.AddDays(2),
            EndTime = DateTime.Now.AddDays(2).AddHours(2),
        }));

        var export = await _factory.WithServiceAsync<IBackupDataService, BackupExportDto>(
            (backup, _) => backup.BuildExportAsync());

        var result = await _factory.WithServiceAsync<IBackupDataService, ServiceResult<BackupImportResponseDto>>(
            (backup, _) => backup.ImportJsonAsync(export, SeededUserId));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Value!.Imported["sessions"]);
        Assert.Equal(1, result.Value.Imported["customCourses"]);
        Assert.False(result.Value.Dropped.ContainsKey("sessions"));

        // The re-inserted course got a NEW id, and the session must point at the new one.
        var newCourseId = await _factory.WithDbAsync(async db =>
            StudyProgramCatalog.CustomCourseIdOffset + await db.CustomCourses.AsNoTracking().Select(c => c.Id).SingleAsync());
        var sessionCourseId = await _factory.WithDbAsync(db => db.Sessions.AsNoTracking().Select(s => s.CourseId).SingleAsync());
        Assert.Equal(newCourseId, sessionCourseId);
    }
}

public class CalendarTokenServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const int SeededUserId = 1;

    public CalendarTokenServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOrCreateAsync_CreatesLazilyThenReturnsTheSameToken()
    {
        var first = await _factory.WithServiceAsync<ICalendarTokenService, CalendarTokenResult>(
            (tokens, _) => tokens.GetOrCreateAsync(SeededUserId));
        var second = await _factory.WithServiceAsync<ICalendarTokenService, CalendarTokenResult>(
            (tokens, _) => tokens.GetOrCreateAsync(SeededUserId));

        Assert.Equal(CalendarTokenOutcome.Ok, first.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(first.Token));
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public async Task RegenerateAsync_ReplacesTheToken()
    {
        var before = await _factory.WithServiceAsync<ICalendarTokenService, CalendarTokenResult>(
            (tokens, _) => tokens.GetOrCreateAsync(SeededUserId));

        var after = await _factory.WithServiceAsync<ICalendarTokenService, CalendarTokenResult>(
            (tokens, _) => tokens.RegenerateAsync(SeededUserId));

        Assert.Equal(CalendarTokenOutcome.Ok, after.Outcome);
        Assert.NotEqual(before.Token, after.Token);
    }

    [Fact]
    public async Task GetOrCreateAsync_UnknownUser_ReportsUnknownUser()
    {
        var result = await _factory.WithServiceAsync<ICalendarTokenService, CalendarTokenResult>(
            (tokens, _) => tokens.GetOrCreateAsync(999999));

        Assert.Equal(CalendarTokenOutcome.UnknownUser, result.Outcome);
        Assert.Null(result.Token);
    }
}

public class DeveloperClientServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DeveloperClientServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CreateDeveloperClientRequestDto ValidClient(string clientId) => new()
    {
        ClientId = clientId,
        Name = "Mein Add-on",
        Description = "Test",
        AllowedRedirectUris = new List<string> { "https://addon.example.com/callback" },
        RequestedScopes = new List<string> { "Sessions.GetAll" },
    };

    [Fact]
    public async Task CreateAsync_ValidClient_IsListedForItsOwner()
    {
        var created = await _factory.WithServiceAsync<IDeveloperClientService, ServiceResult<DeveloperClientDto>>(
            (clients, _) => clients.CreateAsync(ValidClient("my-addon")));

        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        var all = await _factory.WithServiceAsync<IDeveloperClientService, List<DeveloperClientDto>>(
            (clients, _) => clients.GetAllAsync());
        Assert.Contains(all, c => c.ClientId == "my-addon");
    }

    [Fact]
    public async Task CreateAsync_DuplicateClientId_IsInvalid()
    {
        await _factory.WithServiceAsync<IDeveloperClientService>((clients, _) => clients.CreateAsync(ValidClient("dup-addon")));

        var again = await _factory.WithServiceAsync<IDeveloperClientService, ServiceResult<DeveloperClientDto>>(
            (clients, _) => clients.CreateAsync(ValidClient("dup-addon")));

        Assert.Equal(ServiceOutcome.Invalid, again.Outcome);
        Assert.Equal("ClientId 'dup-addon' is already taken.", again.Error);
    }

    [Fact]
    public async Task CreateAsync_NonHttpsNonLoopbackRedirectUri_IsInvalid()
    {
        var request = ValidClient("bad-redirect-addon");
        request.AllowedRedirectUris = new List<string> { "http://evil.example.com/callback" };

        var result = await _factory.WithServiceAsync<IDeveloperClientService, ServiceResult<DeveloperClientDto>>(
            (clients, _) => clients.CreateAsync(request));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Contains("must be an absolute https URL", result.Error!);
    }

    [Fact]
    public async Task UpdateAsync_UnknownClientId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<IDeveloperClientService, ServiceResult<DeveloperClientDto>>(
            (clients, _) => clients.UpdateAsync("does-not-exist", new UpdateDeveloperClientRequestDto
            {
                Name = "X",
                AllowedRedirectUris = new List<string> { "https://a.example.com/cb" },
                RequestedScopes = new List<string> { "Sessions.GetAll" },
            }));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task DeleteAsync_AlsoRemovesTheKeysIssuedForThatClient()
    {
        await _factory.WithServiceAsync<IDeveloperClientService>((clients, _) => clients.CreateAsync(ValidClient("keyed-addon")));
        await _factory.WithDbAsync(async db =>
        {
            db.ClientApiKeys.Add(new ClientApiKeyEntity
            {
                AuthUserId = 1,
                ClientId = "keyed-addon",
                KeyHash = AuthSessionService.HashToken(AuthSessionService.GenerateToken()),
                GrantedScopes = "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var result = await _factory.WithServiceAsync<IDeveloperClientService, ServiceResult>(
            (clients, _) => clients.DeleteAsync("keyed-addon"));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        // An orphaned key would be unrevocable (2026-09 audit S5).
        var leftovers = await _factory.WithDbAsync(db => db.ClientApiKeys.IgnoreQueryFilters()
            .CountAsync(k => k.ClientId == "keyed-addon"));
        Assert.Equal(0, leftovers);
    }
}

public class ConsentRedirectPolicyRedirectUriTests
{
    [Theory]
    [InlineData("https://addon.example.com/cb", true)]
    [InlineData("http://127.0.0.1:8765/cb", true)]
    [InlineData("http://localhost:8765/cb", true)]
    [InlineData("http://evil.example.com/cb", false)]
    [InlineData("ftp://example.com/cb", false)]
    [InlineData("not-a-url", false)]
    [InlineData(null, false)]
    public void IsAllowedRedirectUri_KeepsTheHttpsOrLoopbackRule(string? redirectUri, bool expected) =>
        Assert.Equal(expected, ConsentRedirectPolicy.IsAllowedRedirectUri(redirectUri));
}
