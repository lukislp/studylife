// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Texts;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The per-user language the server sends its own texts in: PUT /api/settings/language, its
/// validation, that the generic settings PUT leaves it alone, that it survives an export/import
/// round trip, and that the senders read it. Own class (own factory/DB): the tests mutate the
/// one settings row and assume it starts without a language.
/// </summary>
public class SettingsLanguageTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsLanguageTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> PutLanguageAsync(string? language) =>
        _client.PutAsJsonAsync("/api/settings/language", new SetLanguageRequest { Language = language! });

    private async Task<string?> StoredLanguageAsync() =>
        await _factory.WithDbAsync(db => db.Settings.AsNoTracking().Select(s => s.Language).FirstOrDefaultAsync());

    [Fact]
    public async Task A_user_who_never_reported_a_language_has_none()
    {
        var dto = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Null(dto!.Language);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt")]
    [InlineData("ga")]
    public async Task Supported_language_is_stored_and_shown_by_get(string language)
    {
        var response = await PutLanguageAsync(language);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(language, await StoredLanguageAsync());
        var dto = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(language, dto!.Language);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData("DE")]      // codes are the lower-case registry entries, no normalising on the server
    [InlineData("pt-BR")]   // no region tags
    [InlineData("deutsch")]
    public async Task Unsupported_language_is_rejected_with_400_and_changes_nothing(string language)
    {
        await PutLanguageAsync("fr");

        var response = await PutLanguageAsync(language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("fr", await StoredLanguageAsync());
    }

    [Fact]
    public async Task Missing_language_in_the_body_is_rejected()
    {
        var response = await _client.PutAsJsonAsync("/api/settings/language", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reporting_the_same_language_again_changes_nothing_and_does_not_bump_the_version()
    {
        await PutLanguageAsync("es");
        var before = (await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!;

        var response = await PutLanguageAsync("es");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = (await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!;
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task Generic_settings_put_cannot_set_or_clear_the_language()
    {
        await PutLanguageAsync("it");
        var settings = (await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!;

        // A stale full-row save from a device that never heard of the field ...
        settings.Language = null;
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", settings)).StatusCode);
        Assert.Equal("it", await StoredLanguageAsync());

        // ... and one that tries to set it through the wrong door.
        settings = (await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!;
        settings.Language = "ru";
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", settings)).StatusCode);
        Assert.Equal("it", await StoredLanguageAsync());
    }

    [Fact]
    public async Task A_language_survives_an_export_and_import_round_trip()
    {
        await PutLanguageAsync("nl");
        var export = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var file = await export.Content.ReadFromJsonAsync<BackupExportDto>();
        Assert.Equal("nl", file!.Settings.Language);

        // Wipe the stored value, then restore it from the file.
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.Settings.SingleAsync();
            row.Language = null;
            await db.SaveChangesAsync();
        });
        var import = await _client.PostAsJsonAsync("/api/backup/import-json", file);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        Assert.Equal("nl", await StoredLanguageAsync());
    }

    [Fact]
    public async Task An_import_with_an_unsupported_language_stores_none()
    {
        var export = await _client.GetAsync("/api/backup/export");
        var file = (await export.Content.ReadFromJsonAsync<BackupExportDto>())!;
        file.Settings.Language = "klingon";

        var import = await _client.PostAsJsonAsync("/api/backup/import-json", file);

        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Null(await StoredLanguageAsync());
    }
}

/// <summary>The senders that run without a request read the language from the user's settings
/// row; the user-scoped worker context is what the existing tests already use.</summary>
public class NotificationLanguageSenderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public NotificationLanguageSenderTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Language_column_is_what_the_text_lookup_resolves_for_the_user()
    {
        await _factory.CreateClient().PutAsJsonAsync("/api/settings/language", new SetLanguageRequest { Language = "fr" });

        var language = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().Select(s => s.Language).FirstAsync());

        Assert.Equal("fr", language);
        Assert.Equal("Content de te revoir 👋", ServerText.Text("comeback.title", language));
    }
}
