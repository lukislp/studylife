using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// GET /api/progress/shared/{token} - the public, read-only progress link reachable without
/// an API key (see ProgressController). All requests here deliberately use a
/// client WITHOUT an X-Api-Key header (ApiKeyTestHelpers.CreateClientWithKey(_factory, null)), to
/// prove that the endpoint is really reachable unauthenticated - that's exactly the
/// purpose of the Program.cs exception.
///
/// All facts in this class share a factory/DB (IClassFixture) and thus the same
/// settings singleton row - each test upserts its own ProgressShareToken via
/// SeedSettingsAsync, so parallel/subsequent tests don't overwrite each other.
/// </summary>
public class ProgressControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymousClient;

    public ProgressControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(factory, null);
    }

    private async Task SeedSettingsAsync(
        bool progressShareEnabled,
        string? progressShareToken,
        string selectedCourseIds = "",
        string completedCourseIds = "")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var entity = await db.Settings.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new UserSettingsEntity();
            db.Settings.Add(entity);
        }
        entity.ProgressShareEnabled = progressShareEnabled;
        entity.ProgressShareToken = progressShareToken;
        entity.SelectedCourseIds = selectedCourseIds;
        entity.CompletedCourseIds = completedCourseIds;
        await db.SaveChangesAsync();
    }

    private async Task SeedCourseGoalAsync(int courseId, decimal? grade, string completedTopics = "")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        db.CourseGoals.Add(new CourseGoalEntity
        {
            CourseId = courseId,
            CourseName = "Test",
            Grade = grade,
            CompletedTopics = completedTopics,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetShared_ValidTokenAndEnabled_ReturnsProgressSnapshotWithoutApiKey()
    {
        // Course 3 ("Mathematik: Analysis", 5 ECTS, ungrouped) completed with grade 1.7;
        // courses 1 + 2 active (SelectedCourseIds), see CourseCatalog.AppliedAICourses.
        await SeedSettingsAsync(
            progressShareEnabled: true,
            progressShareToken: "share-token-valid",
            selectedCourseIds: "1,2",
            completedCourseIds: "3");
        await SeedCourseGoalAsync(courseId: 3, grade: 1.7m);

        var response = await _anonymousClient.GetAsync("/api/progress/shared/share-token-valid");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProgressShareDto>();
        Assert.NotNull(dto);
        Assert.Equal(CourseCatalog.CalcTotalEcts(CourseCatalog.AppliedAICourses), dto!.TotalEcts);
        Assert.Equal(5, dto.EarnedEcts); // course 3 alone, 5 ECTS
        Assert.Equal(1.7m, dto.AverageGrade);
        Assert.Equal(1, dto.CoursesCompletedCount);
        Assert.Equal(2, dto.ActiveCourses.Count);
        Assert.Contains(dto.ActiveCourses, c => c.Name == "Artificial Intelligence");
        Assert.Contains(dto.ActiveCourses, c => c.Name == "Einführung in die Programmierung mit Python");
        // No notes/sessions/settings in the DTO - the type itself already has no fields for
        // that, here additionally verified that no course outside the active selection shows up.
        Assert.DoesNotContain(dto.ActiveCourses, c => c.Name == "Mathematik: Analysis");
    }

    [Fact]
    public async Task GetShared_InvalidToken_ReturnsNotFound()
    {
        await SeedSettingsAsync(progressShareEnabled: true, progressShareToken: "share-token-real");

        var response = await _anonymousClient.GetAsync("/api/progress/shared/some-wrong-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetShared_FeatureDisabled_ReturnsNotFoundEvenWithCorrectToken()
    {
        await SeedSettingsAsync(progressShareEnabled: false, progressShareToken: "share-token-disabled");

        var response = await _anonymousClient.GetAsync("/api/progress/shared/share-token-disabled");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetShared_NoTokenEverGenerated_ReturnsNotFound()
    {
        await SeedSettingsAsync(progressShareEnabled: false, progressShareToken: null);

        var response = await _anonymousClient.GetAsync("/api/progress/shared/anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// New pinning test (audit finding A3 refactor): the share link is reachable WITHOUT any
    /// credential (every other fact in this class proves that), but an X-Session-Token that IS
    /// present and invalid must still be rejected with 401, not fall through to the normal
    /// token-lookup-based 404 - see the "PublicUnlessInvalidSession" policy
    /// (Auth/StudyLifeAuthorizationPolicies.cs), which exists specifically to reproduce this
    /// quirk of the former resolution middleware. Previously untested.
    /// </summary>
    [Fact]
    public async Task GetShared_WithInvalidSessionToken_ReturnsUnauthorized()
    {
        await SeedSettingsAsync(progressShareEnabled: true, progressShareToken: "share-token-real");

        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        client.DefaultRequestHeaders.Add("X-Session-Token", "not-a-real-token");

        var response = await client.GetAsync("/api/progress/shared/share-token-real");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// POST /api/settings/progress-share/{enable,regenerate,disable} - unlike GetShared
/// above, these ARE under the normal API-key requirement (only the owner may toggle), so
/// CustomWebApplicationFactory's default client configuration is sufficient here.
/// </summary>
public class ProgressShareSettingsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProgressShareSettingsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Enable_FirstTime_GeneratesTokenAndEnablesFeature()
    {
        var response = await _client.PostAsync("/api/settings/progress-share/enable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(dto);
        Assert.True(dto!.ProgressShareEnabled);
        Assert.False(string.IsNullOrEmpty(dto.ProgressShareToken));
    }

    [Fact]
    public async Task Disable_ThenEnable_GeneratesAFreshToken_OldTokenNeverWorksAgain()
    {
        // Deliberate behavior (no longer "same token after reactivating"): a once
        // shared/leaked link must not become valid again by disabling+re-enabling -
        // see the SettingsController.DisableProgressShare comment.
        var first = await (await _client.PostAsync("/api/settings/progress-share/enable", null))
            .Content.ReadFromJsonAsync<UserSettingsDto>();
        var oldToken = first!.ProgressShareToken!;

        await _client.PostAsync("/api/settings/progress-share/disable", null);
        var second = await (await _client.PostAsync("/api/settings/progress-share/enable", null))
            .Content.ReadFromJsonAsync<UserSettingsDto>();

        Assert.NotEqual(oldToken, second!.ProgressShareToken);

        var anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var oldTokenResponse = await anonymousClient.GetAsync($"/api/progress/shared/{oldToken}");
        Assert.Equal(HttpStatusCode.NotFound, oldTokenResponse.StatusCode);

        var newTokenResponse = await anonymousClient.GetAsync($"/api/progress/shared/{second.ProgressShareToken}");
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }

    [Fact]
    public async Task Regenerate_ChangesToken_AndOldTokenNoLongerWorksOnSharedEndpoint()
    {
        var enabled = await (await _client.PostAsync("/api/settings/progress-share/enable", null))
            .Content.ReadFromJsonAsync<UserSettingsDto>();
        var oldToken = enabled!.ProgressShareToken!;

        var regenerateResponse = await _client.PostAsync("/api/settings/progress-share/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, regenerateResponse.StatusCode);
        var regenerated = await regenerateResponse.Content.ReadFromJsonAsync<UserSettingsDto>();

        Assert.NotEqual(oldToken, regenerated!.ProgressShareToken);
        Assert.True(regenerated.ProgressShareEnabled);

        // The old token must be immediately dead on the public endpoint, the new one must work.
        var anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var oldTokenResponse = await anonymousClient.GetAsync($"/api/progress/shared/{oldToken}");
        Assert.Equal(HttpStatusCode.NotFound, oldTokenResponse.StatusCode);

        var newTokenResponse = await anonymousClient.GetAsync($"/api/progress/shared/{regenerated.ProgressShareToken}");
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }

    [Fact]
    public async Task Disable_DisablesFeature_ButGetReflectsItImmediately()
    {
        await _client.PostAsync("/api/settings/progress-share/enable", null);

        var disableResponse = await _client.PostAsync("/api/settings/progress-share/disable", null);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disabled = await disableResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.True(string.IsNullOrEmpty(disabled!.ProgressShareToken));

        var getResponse = await _client.GetAsync("/api/settings");
        var dto = await getResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.False(dto!.ProgressShareEnabled);
    }
}

/// <summary>
/// Security regression test: GetShared must resolve the ACTUAL token owner (via
/// IgnoreQueryFilters + BeginBackgroundScope), not the phase 1 fallback user ambiently resolved
/// by the gate (always the first AuthUser) - otherwise the progress share link of
/// every user except the very first one would 404 for no reason, because the token comparison
/// runs against the wrong settings row. Own factory (fresh DB), because a real
/// two-user situation is needed via the passkey registration flow.
/// </summary>
public class ProgressControllerMultiUserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProgressControllerMultiUserTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetShared_ForSecondRegisteredUser_ReturnsTheirOwnData_NotTheFirstUsers()
    {
        // First user claims the legacy user, second creates a brand-new account
        // (registration case distinction, see PasskeyRegistrationTests).
        using var firstKey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        var (alexId, annaId) = await _factory.WithDbAsync(async db =>
        {
            var alex = await db.AuthUsers.SingleAsync(u => u.DisplayName == "Alex");
            var anna = await db.AuthUsers.SingleAsync(u => u.DisplayName == "Anna");

            // AuthUserId set explicitly instead of via BeginBackgroundScope: both usings would
            // have already exited by the time the single SaveChangesAsync below runs, so
            // StampAuthUserIdOnAddedEntries' one-time-memoized ambient lookup would stamp BOTH
            // rows with whatever user happens to be ambient at that point instead of each
            // one's actual owner - harmless before the AddPerUserUniqueRows migration (both
            // rows just silently got the same wrong AuthUserId, masked by GetShared querying
            // by token, not AuthUserId), but a genuine UNIQUE constraint violation since.
            db.Settings.Add(new UserSettingsEntity
            {
                AuthUserId = alex.Id,
                ProgressShareEnabled = true,
                ProgressShareToken = "alex-share-token",
                SelectedCourseIds = "",
                CompletedCourseIds = "3", // Mathematik: Analysis, 5 ECTS
            });
            db.Settings.Add(new UserSettingsEntity
            {
                AuthUserId = anna.Id,
                ProgressShareEnabled = true,
                ProgressShareToken = "anna-share-token",
                SelectedCourseIds = "1,2",
                CompletedCourseIds = "",
            });
            await db.SaveChangesAsync();
            return (alex.Id, anna.Id);
        });

        var anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        // Before the fix: 404, because GetShared always compared against Alex' (first user)
        // settings row, regardless of the token actually passed in.
        var annaResponse = await anonymousClient.GetAsync("/api/progress/shared/anna-share-token");
        Assert.Equal(HttpStatusCode.OK, annaResponse.StatusCode);
        var annaDto = await annaResponse.Content.ReadFromJsonAsync<ProgressShareDto>();
        Assert.NotNull(annaDto);
        Assert.Equal(0, annaDto!.CoursesCompletedCount);
        Assert.Equal(2, annaDto.ActiveCourses.Count);

        // Control: Alex' own link still returns his own, differently-shaped data.
        var alexResponse = await anonymousClient.GetAsync("/api/progress/shared/alex-share-token");
        Assert.Equal(HttpStatusCode.OK, alexResponse.StatusCode);
        var alexDto = await alexResponse.Content.ReadFromJsonAsync<ProgressShareDto>();
        Assert.NotNull(alexDto);
        Assert.Equal(1, alexDto!.CoursesCompletedCount);
        Assert.Empty(alexDto.ActiveCourses);
    }
}

/// <summary>
/// GetShared with an ACTIVE CUSTOM STUDY PROGRAM (settings.ActiveStudyProgramId set): the
/// course list/group quotas must come from StudyProgramCatalog (the user's own program
/// tables) instead of the built-in CourseCatalog. Own class/factory: the other progress
/// tests above rely on the built-in catalog and never reset ActiveStudyProgramId.
/// </summary>
public class ProgressControllerStudyProgramShareTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymousClient;

    public ProgressControllerStudyProgramShareTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(factory, null);
    }

    [Fact]
    public async Task GetShared_WithActiveCustomStudyProgram_UsesProgramCatalogInsteadOfBuiltIn()
    {
        var programId = await _factory.WithDbAsync(async db =>
        {
            var program = new StudyProgramEntity
            {
                AuthUserId = 1, // seeded test user
                Name = "Custom Test Program",
                CreatedAt = DateTime.UtcNow,
            };
            db.StudyPrograms.Add(program);
            await db.SaveChangesAsync();

            var entity = await db.Settings.FirstOrDefaultAsync();
            if (entity == null)
            {
                entity = new UserSettingsEntity();
                db.Settings.Add(entity);
            }
            entity.ProgressShareEnabled = true;
            entity.ProgressShareToken = "program-share-token";
            entity.ActiveStudyProgramId = program.Id;
            entity.SelectedCourseIds = "";
            entity.CompletedCourseIds = "";
            await db.SaveChangesAsync();
            return program.Id;
        });

        var response = await _anonymousClient.GetAsync("/api/progress/shared/program-share-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProgressShareDto>();
        Assert.NotNull(dto);
        // An empty custom program has no courses at all - completely different shape than the
        // built-in catalog (which always carries its full fixed course list).
        Assert.Equal(0, dto!.TotalEcts);
        Assert.Equal(0, dto.EarnedEcts);
        Assert.Equal(0, dto.CoursesTotalCount);
        Assert.Empty(dto.ActiveCourses);
        Assert.True(programId > 0);
    }
}
