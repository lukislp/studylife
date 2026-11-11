// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The first motivation style was renamed (see <see cref="MotivationalStyles"/>): the old stored
/// value must keep working in the database, on the settings API (older clients, native apps) and in
/// backup files, while everything new is written with the neutral key. Own factory: the tests
/// overwrite the singleton settings row.
/// </summary>
public class MotivationalStyleCompatibilityTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MotivationalStyleCompatibilityTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<UserSettingsDto> GetSettingsAsync() =>
        (await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!;

    private async Task SetStoredStyleAsync(string style)
    {
        // Make sure the row exists, then write the raw value the way an old release stored it.
        await GetSettingsAsync();
        await _factory.WithDbAsync(async db =>
        {
            var row = db.Settings.Single();
            row.MotivationalStyle = style;
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task OldStoredValue_IsReturnedAsTheNewKey()
    {
        await SetStoredStyleAsync(MotivationalStyles.LegacyThoughtful);

        Assert.Equal(MotivationalStyles.Thoughtful, (await GetSettingsAsync()).MotivationalStyle);
    }

    [Fact]
    public async Task OldValueFromAnOlderClient_IsStoredAsTheNewKey()
    {
        var settings = await GetSettingsAsync();
        settings.MotivationalStyle = MotivationalStyles.LegacyThoughtful;
        settings.Version = null;
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        var stored = await _factory.WithDbAsync(db => Task.FromResult(db.Settings.Single().MotivationalStyle));
        Assert.Equal(MotivationalStyles.Thoughtful, stored);
    }

    [Theory]
    [InlineData("thoughtful")]
    [InlineData("zen")]
    [InlineData("intense")]
    [InlineData("hype")]
    public async Task NewValue_RoundTrips(string style)
    {
        var settings = await GetSettingsAsync();
        settings.MotivationalStyle = style;
        settings.Version = null;
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        Assert.Equal(style, (await GetSettingsAsync()).MotivationalStyle);
    }

    [Fact]
    public async Task BackupFromBeforeTheRename_ImportsWithTheNewKey()
    {
        var exportResponse = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var export = (await exportResponse.Content.ReadFromJsonAsync<BackupExportDto>())!;
        export.Settings.MotivationalStyle = MotivationalStyles.LegacyThoughtful;

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/backup/import-json", export)).StatusCode);

        Assert.Equal(MotivationalStyles.Thoughtful, (await GetSettingsAsync()).MotivationalStyle);
        var stored = await _factory.WithDbAsync(db => Task.FromResult(db.Settings.Single().MotivationalStyle));
        Assert.Equal(MotivationalStyles.Thoughtful, stored);
    }

    [Fact]
    public async Task ExportWritesTheNewKey()
    {
        await SetStoredStyleAsync(MotivationalStyles.LegacyThoughtful);

        var export = (await _client.GetFromJsonAsync<BackupExportDto>("/api/backup/export"))!;

        Assert.Equal(MotivationalStyles.Thoughtful, export.Settings.MotivationalStyle);
    }

    [Fact]
    public void DailyQuote_OfTheOldValue_IsTheThoughtfulPool()
    {
        var date = new DateTime(2026, 7, 15);

        Assert.Equal(
            BackgroundTaskService.PickDailyMotivationQuote(MotivationalStyles.Thoughtful, date),
            BackgroundTaskService.PickDailyMotivationQuote(MotivationalStyles.LegacyThoughtful, date));
    }
}
