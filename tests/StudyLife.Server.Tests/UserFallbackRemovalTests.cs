using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Regression coverage for audit finding A2 (severity high): Program.cs's "current user"
/// resolution middleware used to fall back to the FIRST AuthUserEntity
/// (db.AuthUsers.OrderBy(u => u.Id).FirstOrDefaultAsync()) whenever an /api request reached it
/// without a resolved user. Only ProgressController.GetShared ever exercised that path, and it
/// was harmless there only because GetShared re-resolves the real token owner itself via
/// IgnoreQueryFilters()+BeginBackgroundScope - but any FUTURE gate exemption that forgot to do
/// the same would have silently run with the FIRST user's full access. The fallback has been
/// removed: an unresolved user now stays AuthUserId 0 (ICurrentUserAccessor's documented
/// "no user resolved" value), which the global query filters never match against a real row.
/// </summary>
public class UserFallbackRemovalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public UserFallbackRemovalTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// The one production endpoint that is genuinely reachable anonymously (no session, no API
    /// key) and would previously have hit the removed fallback: GET /api/progress/shared/{token}.
    /// Must keep working exactly as before - GetShared never depended on the ambient/fallback
    /// user in the first place (it looks the token owner up itself), so removing the fallback
    /// must be a no-op for this endpoint.
    /// </summary>
    [Fact]
    public async Task GetShared_AnonymousWithValidToken_StillWorksWithoutTheRemovedFallback()
    {
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Settings.FirstOrDefaultAsync() ?? new UserSettingsEntity();
            if (entity.Id == 0) db.Settings.Add(entity);
            entity.ProgressShareEnabled = true;
            entity.ProgressShareToken = "a2-regression-token";
            entity.SelectedCourseIds = "";
            entity.CompletedCourseIds = "";
            await db.SaveChangesAsync();
        });

        var anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anonymousClient.GetAsync("/api/progress/shared/a2-regression-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Direct proof of the safety net the removal now relies on: with AuthUserId 0 (exactly
    /// what an unresolved request gets today, instead of silently becoming the first real user),
    /// every tenant-filtered query returns nothing - even though a real first user (Id 1, seeded
    /// by CustomWebApplicationFactory/the multi-tenant migration) has real data. Before the fix,
    /// a future exempt-but-userless request would instead have gotten AuthUserId 1 assigned by
    /// Program.cs and this query would have returned the first user's row.
    /// </summary>
    [Fact]
    public async Task QueryFilters_WithUnresolvedAuthUserIdZero_SeeNoDataDespiteFirstUserHavingRealData()
    {
        await _factory.WithDbAsync(async db =>
        {
            using (CurrentUserAccessor.BeginBackgroundScope(1))
            {
                db.Notes.Add(new NoteEntity
                {
                    Title = "A2 regression - first user's note",
                    Content = "Must never be visible to an unresolved (AuthUserId 0) caller.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            // Control: the first user's own context still sees it.
            using (CurrentUserAccessor.BeginBackgroundScope(1))
                Assert.True(await db.Notes.AnyAsync(n => n.Title == "A2 regression - first user's note"));

            // AuthUserId 0 = "no user resolved" (the state Program.cs now leaves an
            // exempt-but-userless request in, instead of the removed first-user fallback).
            using (CurrentUserAccessor.BeginBackgroundScope(0))
                Assert.False(await db.Notes.AnyAsync(n => n.Title == "A2 regression - first user's note"));
        });
    }
}
