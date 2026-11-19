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
/// Proof of the telemetry answer (2026-11-17 GDPR audit F-8): every change of the answer - grant,
/// decline, withdrawal - is stored with its time and the consent text version.
/// </summary>
public class TelemetryConsentRecordTests
{
    private static readonly DateTime T1 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = T1.AddDays(3);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstAnswer_IsStampedWithTimeAndVersion(bool answer)
    {
        var settings = new UserSettingsEntity();

        var changed = TelemetryConsentRecord.Apply(settings, answer, T1);

        Assert.True(changed);
        Assert.Equal(answer, settings.TelemetryConsent);
        Assert.Equal(T1, settings.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, settings.TelemetryConsentVersion);
    }

    [Fact]
    public void SameAnswerAgain_KeepsTheOriginalProof()
    {
        var settings = new UserSettingsEntity();
        TelemetryConsentRecord.Apply(settings, true, T1);

        var changed = TelemetryConsentRecord.Apply(settings, true, T2);

        Assert.False(changed);
        Assert.Equal(T1, settings.TelemetryConsentAt);
    }

    [Fact]
    public void Withdrawal_IsStampedLikeAGrant()
    {
        var settings = new UserSettingsEntity();
        TelemetryConsentRecord.Apply(settings, true, T1);

        var changed = TelemetryConsentRecord.Apply(settings, false, T2);

        Assert.True(changed);
        Assert.False(settings.TelemetryConsent);
        Assert.Equal(T2, settings.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, settings.TelemetryConsentVersion);
    }

    [Fact]
    public void SameAnswerToANewerText_IsANewAnswer()
    {
        // A "yes" given to the old text does not cover the new one: re-answering is recorded.
        var settings = new UserSettingsEntity
        {
            TelemetryConsent = true,
            TelemetryConsentAt = T1,
            TelemetryConsentVersion = TelemetryConsentRecord.CurrentVersion - 1,
        };

        var changed = TelemetryConsentRecord.Apply(settings, true, T2);

        Assert.True(changed);
        Assert.Equal(T2, settings.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, settings.TelemetryConsentVersion);
    }
}

/// <summary>
/// PUT /api/settings/telemetry-consent: the one way to change the answer. It needs a real session
/// (an API key such as the Home Assistant key cannot reach it), the generic settings PUT ignores
/// the field, and every change is appended to the consent log instead of overwriting it.
/// </summary>
public class TelemetryConsentEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Route = "/api/settings/telemetry-consent";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TelemetryConsentEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> AnswerAsync(bool consent) =>
        _client.PutAsJsonAsync(Route, new SetTelemetryConsentRequest { Consent = consent });

    private Task<UserSettingsEntity> StoredAsync() =>
        _factory.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync());

    private Task<List<TelemetryConsentLogEntity>> LogAsync() =>
        _factory.WithDbAsync(db => db.TelemetryConsentLog.AsNoTracking().OrderBy(l => l.Id).ToListAsync());

    private Task ResetAsync() => _factory.WithDbAsync(async db =>
    {
        await db.TelemetryConsentLog.ExecuteDeleteAsync();
        await db.Settings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.TelemetryConsent, (bool?)null)
            .SetProperty(x => x.TelemetryConsentAt, (DateTime?)null)
            .SetProperty(x => x.TelemetryConsentVersion, (int?)null));
    });

    [Fact]
    public async Task GrantThenWithdraw_AppendsBothAnswers_AndKeepsTheCurrentOneInSettings()
    {
        await ResetAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.NoContent, (await AnswerAsync(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AnswerAsync(false)).StatusCode);

        var log = await LogAsync();
        Assert.Collection(log,
            granted => { Assert.True(granted.Answer); Assert.Equal(TelemetryConsentRecord.CurrentVersion, granted.TextVersion); Assert.True(granted.AnsweredAt >= before); },
            withdrawn => { Assert.False(withdrawn.Answer); Assert.Equal(TelemetryConsentRecord.CurrentVersion, withdrawn.TextVersion); Assert.True(withdrawn.AnsweredAt >= log[0].AnsweredAt); });
        var stored = await StoredAsync();
        Assert.False(stored.TelemetryConsent);
        Assert.Equal(log[1].AnsweredAt, stored.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, stored.TelemetryConsentVersion);
    }

    [Fact]
    public async Task RepeatingTheSameAnswer_AddsNoLogEntry()
    {
        await ResetAsync();
        await AnswerAsync(true);
        var first = (await LogAsync()).Single();

        Assert.Equal(HttpStatusCode.NoContent, (await AnswerAsync(true)).StatusCode);

        var log = await LogAsync();
        Assert.Single(log);
        Assert.Equal(first.AnsweredAt, log[0].AnsweredAt);
    }

    [Fact]
    public async Task AnAnswerGivenToAnOlderText_IsAnsweredAgainAsANewLogEntry()
    {
        await ResetAsync();
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.Settings.SingleAsync();
            row.TelemetryConsent = true;
            row.TelemetryConsentAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
            row.TelemetryConsentVersion = 1;
            await db.SaveChangesAsync();
        });

        await AnswerAsync(true);

        var entry = Assert.Single(await LogAsync());
        Assert.True(entry.Answer);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, entry.TextVersion);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, (await StoredAsync()).TelemetryConsentVersion);
    }

    [Fact]
    public async Task TheGenericSettingsPut_IgnoresTheField_AndWritesNoLogEntry()
    {
        await ResetAsync();
        await AnswerAsync(false);

        var settings = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        settings!.TelemetryConsent = true;
        settings.Theme = settings.Theme == "dark" ? "light" : "dark"; // an unrelated change rides along
        var response = await _client.PutAsJsonAsync("/api/settings", settings);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<UserSettingsDto>())!.TelemetryConsent);
        Assert.False((await StoredAsync()).TelemetryConsent);
        Assert.False(Assert.Single(await LogAsync()).Answer);
    }

    [Fact]
    public async Task AnApiKey_CannotAnswer_NorGrantAWithdrawnConsent()
    {
        await ResetAsync();
        await AnswerAsync(false);
        var generated = await _client.PostAsync("/api/settings/ha-api-key/generate", null);
        var apiKey = (await generated.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>())!.ApiKey;

        try
        {
            using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

            var viaEndpoint = await keyClient.PutAsJsonAsync(Route, new SetTelemetryConsentRequest { Consent = true });
            var settings = await keyClient.GetFromJsonAsync<UserSettingsDto>("/api/settings");
            settings!.TelemetryConsent = true;
            var viaGenericPut = await keyClient.PutAsJsonAsync("/api/settings", settings);

            Assert.Equal(HttpStatusCode.Unauthorized, viaEndpoint.StatusCode);
            Assert.Equal(HttpStatusCode.OK, viaGenericPut.StatusCode); // reachable, but the field is ignored
            Assert.False((await StoredAsync()).TelemetryConsent);
            Assert.False(Assert.Single(await LogAsync()).Answer);
        }
        finally
        {
            await _client.PostAsync("/api/settings/ha-api-key/revoke", null);
        }
    }

    [Fact]
    public async Task ABodyWithoutTheAnswer_IsRejected_NotReadAsNo()
    {
        await ResetAsync();
        await AnswerAsync(true);

        var response = await _client.PutAsJsonAsync(Route, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await StoredAsync()).TelemetryConsent);
        Assert.Single(await LogAsync());
    }

    [Fact]
    public async Task TheAnswer_IsReadBackThroughSettings_AfterAChange()
    {
        await ResetAsync();
        await AnswerAsync(true);
        Assert.True((await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!.TelemetryConsent);

        await AnswerAsync(false);

        Assert.False((await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings"))!.TelemetryConsent);
    }
}