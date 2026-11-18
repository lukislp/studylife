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
/// decline, withdrawal - is stored with its time and the consent text version; "never asked"
/// (null) carries neither.
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

        TelemetryConsentRecord.Apply(settings, answer, T1);

        Assert.Equal(answer, settings.TelemetryConsent);
        Assert.Equal(T1, settings.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, settings.TelemetryConsentVersion);
    }

    [Fact]
    public void SameAnswerAgain_KeepsTheOriginalProof()
    {
        // Every settings PUT carries the answer; only a change may move the timestamp.
        var settings = new UserSettingsEntity();
        TelemetryConsentRecord.Apply(settings, true, T1);

        TelemetryConsentRecord.Apply(settings, true, T2);

        Assert.Equal(T1, settings.TelemetryConsentAt);
    }

    [Fact]
    public void Withdrawal_IsStampedLikeAGrant()
    {
        var settings = new UserSettingsEntity();
        TelemetryConsentRecord.Apply(settings, true, T1);

        TelemetryConsentRecord.Apply(settings, false, T2);

        Assert.False(settings.TelemetryConsent);
        Assert.Equal(T2, settings.TelemetryConsentAt);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, settings.TelemetryConsentVersion);
    }

    [Fact]
    public void BackToNeverAsked_ClearsTheProof()
    {
        var settings = new UserSettingsEntity();
        TelemetryConsentRecord.Apply(settings, true, T1);

        TelemetryConsentRecord.Apply(settings, null, T2);

        Assert.Null(settings.TelemetryConsent);
        Assert.Null(settings.TelemetryConsentAt);
        Assert.Null(settings.TelemetryConsentVersion);
    }

    [Fact]
    public void NeverAsked_StaysWithoutProof()
    {
        var settings = new UserSettingsEntity();

        TelemetryConsentRecord.Apply(settings, null, T1);

        Assert.Null(settings.TelemetryConsentAt);
        Assert.Null(settings.TelemetryConsentVersion);
    }
}

/// <summary>The settings PUT - the path the consent modal and the Setup card use - records the
/// proof, and only when the answer changes.</summary>
public class TelemetryConsentSettingsPutTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TelemetryConsentSettingsPutTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task PutConsentAsync(bool? consent)
    {
        var settings = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        settings!.TelemetryConsent = consent;
        settings.Theme = settings.Theme == "dark" ? "light" : "dark"; // an unrelated change rides along
        var response = await _client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<UserSettingsEntity> StoredAsync() =>
        _factory.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync());

    [Fact]
    public async Task GrantThenWithdraw_EachIsRecorded_UnrelatedSavesKeepTheProof()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await PutConsentAsync(true);
        var granted = await StoredAsync();
        Assert.True(granted.TelemetryConsent);
        Assert.True(granted.TelemetryConsentAt >= before);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, granted.TelemetryConsentVersion);

        // Backdate the grant so a restamp is visible regardless of clock resolution.
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.Settings.SingleAsync();
            row.TelemetryConsentAt = longAgo;
            await db.SaveChangesAsync();
        });

        await PutConsentAsync(true);
        Assert.Equal(longAgo, (await StoredAsync()).TelemetryConsentAt);

        await PutConsentAsync(false);
        var withdrawn = await StoredAsync();
        Assert.False(withdrawn.TelemetryConsent);
        Assert.True(withdrawn.TelemetryConsentAt >= before);
        Assert.Equal(TelemetryConsentRecord.CurrentVersion, withdrawn.TelemetryConsentVersion);
    }
}
