// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Audit 2026-11-16 L5: the calendar token and the progress-share token are stored only as
/// hashes, and the startup data step (PlaintextLinkTokenUpgrade) moves tokens from the releases
/// before into the hash columns - so calendar subscriptions and shared links handed out before the
/// upgrade keep working, and running the step again changes nothing.
/// </summary>
public class LinkTokenHashingTests : IClassFixture<CustomWebApplicationFactory>
{
    private const int SeededUserId = 1;
    private readonly CustomWebApplicationFactory _factory;

    public LinkTokenHashingTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LegacyCalendarToken_IsHashedOnUpgrade_AndTheOldSubscriptionUrlKeepsWorking()
    {
        const string legacy = "legacy-calendar-token-from-an-older-release";
        await _factory.WithDbAsync(db => db.AuthUsers.Where(u => u.Id == SeededUserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LegacyCalendarToken, legacy)
                .SetProperty(u => u.CalendarTokenHash, (string?)null)));

        var moved = await _factory.WithDbAsync(db => PlaintextLinkTokenUpgrade.RunAsync(db));
        Assert.True(moved >= 1);

        var stored = await _factory.WithDbAsync(db => db.AuthUsers.AsNoTracking()
            .Where(u => u.Id == SeededUserId)
            .Select(u => new { u.CalendarTokenHash, u.LegacyCalendarToken }).SingleAsync());
        Assert.Null(stored.LegacyCalendarToken);
        Assert.Equal(AuthSessionService.HashToken(legacy), stored.CalendarTokenHash);

        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var ics = await anonymous.GetAsync($"/api/sessions/ics?calendarToken={legacy}");
        Assert.Equal(HttpStatusCode.OK, ics.StatusCode);

        // Presenting the stored hash itself must not authenticate.
        var byHash = await anonymous.GetAsync($"/api/sessions/ics?calendarToken={stored.CalendarTokenHash}");
        Assert.Equal(HttpStatusCode.Unauthorized, byHash.StatusCode);

        // Idempotent: nothing left to move, and the hash stays as it is.
        Assert.Equal(0, await _factory.WithDbAsync(db => PlaintextLinkTokenUpgrade.RunAsync(db)));
        var again = await _factory.WithDbAsync(db => db.AuthUsers.AsNoTracking()
            .Where(u => u.Id == SeededUserId).Select(u => u.CalendarTokenHash).SingleAsync());
        Assert.Equal(stored.CalendarTokenHash, again);
    }

    [Fact]
    public async Task LegacyShareToken_IsHashedOnUpgrade_AndTheOldSharedLinkKeepsWorking()
    {
        const string legacy = "legacy-share-token-from-an-older-release";
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Settings.FirstOrDefaultAsync();
            if (entity == null)
            {
                entity = new UserSettingsEntity();
                db.Settings.Add(entity);
            }
            entity.ProgressShareEnabled = true;
            entity.ProgressShareTokenHash = null;
            entity.LegacyProgressShareToken = legacy;
            entity.SelectedCourseIds = "";
            entity.CompletedCourseIds = "";
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.WithDbAsync(db => PlaintextLinkTokenUpgrade.RunAsync(db));

        var row = await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.ProgressShareTokenHash == AuthSessionService.HashToken(legacy)));
        Assert.Null(row.LegacyProgressShareToken);

        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/progress/shared/{legacy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/progress/shared/{row.ProgressShareTokenHash}")).StatusCode);
    }

    [Fact]
    public async Task CalendarEndpoints_ShowTheTokenOnlyOnCreate()
    {
        var client = _factory.CreateClient();

        var created = await client.PostAsync("/api/system/regenerate-calendar-token", null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<RegenerateCalendarTokenResponseDto>())!.CalendarToken;
        Assert.False(string.IsNullOrWhiteSpace(token));

        var status = await client.GetFromJsonAsync<CalendarTokenResponseDto>("/api/system/calendar-token");
        Assert.True(status!.Exists);
        Assert.NotNull(status.CreatedAt);
        Assert.Null(status.CalendarToken);
        Assert.DoesNotContain(token, await client.GetStringAsync("/api/system/calendar-token"));

        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/sessions/ics?calendarToken={token}")).StatusCode);
    }
}
