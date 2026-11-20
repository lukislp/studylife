// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Services;
using static StudyLife.Client.Tests.AiConsentFixtures;

namespace StudyLife.Client.Tests;

/// <summary>
/// The telemetry question shown after the first sign-in must never trap the user (also not the
/// App Store reviewer): a failed save shows an error and a "later" way out, a server without the
/// endpoint (older than 3.38.0) lets the user straight through, and settings that are only the
/// built-in defaults never trigger the question.
/// </summary>
public class TelemetryConsentModalTests : ConsentBunitTest
{
    private const string Endpoint = "PUT api/settings/telemetry-consent";

    private (IRenderedComponent<TelemetryConsentModal> Cut, Counter Answered, AppStateService State) RenderModal(ScriptedServer server)
    {
        var state = Register(Ctx, server);
        var answered = new Counter();
        var cut = Render<TelemetryConsentModal>(p => p
            .Add(c => c.Show, true)
            .Add(c => c.OnAnswered, () => answered.Count++));
        return (cut, answered, state);
    }

    private sealed class Counter { public int Count; }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public void SavedAnswer_ClosesTheDialog()
    {
        var server = new ScriptedServer().On(Endpoint, _ => Status(HttpStatusCode.OK))
            .OnJson("GET api/settings", new StudyLife.Shared.UserSettingsDto());
        var (cut, answered, _) = RenderModal(server);

        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, answered.Count), ChainTimeout);
        Assert.Empty(cut.FindAll("[data-testid=telemetry-consent-error]"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void FailedSave_ShowsAnError_AndLaterClosesWithoutRecordingADecision(HttpStatusCode failure)
    {
        var server = new ScriptedServer().On(Endpoint, _ => Status(failure));
        var (cut, answered, _) = RenderModal(server);
        Assert.Empty(cut.FindAll("[data-testid=telemetry-consent-later]"));

        cut.Find("button.btn-primary").Click();
        cut.WaitForElement("[data-testid=telemetry-consent-error]", ChainTimeout);

        // Still open, and the buttons work again: the user can retry or leave.
        Assert.Equal(0, answered.Count);
        Assert.All(cut.FindAll("button"), b => Assert.False(b.HasAttribute("disabled")));
        cut.Find("[data-testid=telemetry-consent-later]").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, answered.Count), ChainTimeout);
        Assert.Equal(1, server.Count(Endpoint)); // "later" sends nothing
    }

    [Fact]
    public void Offline_ShowsTheSameWayOut()
    {
        var server = new ScriptedServer().On(Endpoint, _ => throw new HttpRequestException("offline"));
        var (cut, answered, _) = RenderModal(server);

        cut.Find("button.btn-secondary").Click();
        cut.WaitForElement("[data-testid=telemetry-consent-later]", ChainTimeout);
        cut.Find("[data-testid=telemetry-consent-later]").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, answered.Count), ChainTimeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public void OlderServerWithoutTheEndpoint_DoesNotBlock(HttpStatusCode code)
    {
        var server = new ScriptedServer().On(Endpoint, _ => Status(code));
        var (cut, answered, _) = RenderModal(server);

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, answered.Count), ChainTimeout);
        Assert.Empty(cut.FindAll("[data-testid=telemetry-consent-error]"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, TelemetryConsentSaveResult.Saved)]
    [InlineData(HttpStatusCode.NotFound, TelemetryConsentSaveResult.Unavailable)]
    [InlineData(HttpStatusCode.MethodNotAllowed, TelemetryConsentSaveResult.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, TelemetryConsentSaveResult.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, TelemetryConsentSaveResult.Failed)]
    public async Task TrySetTelemetryConsent_ClassifiesTheServerAnswer(HttpStatusCode code, TelemetryConsentSaveResult expected)
    {
        var server = new ScriptedServer().On(Endpoint, _ => Status(code))
            .OnJson("GET api/settings", new StudyLife.Shared.UserSettingsDto());
        var state = NewAppState(server);

        Assert.Equal(expected, await state.TrySetTelemetryConsentAsync(true));
    }

    [Fact]
    public async Task DefaultSettings_AreFlaggedAsFallback_WhenNeitherServerNorCacheAnswer()
    {
        var offline = NewAppState(new ScriptedServer().On("GET api/settings", _ => throw new HttpRequestException("offline")));
        await offline.FetchSettingsFromServerAsync();
        Assert.True(offline.SettingsAreDefaultFallback);

        var online = NewAppState(new ScriptedServer().OnJson("GET api/settings", new StudyLife.Shared.UserSettingsDto()));
        await online.FetchSettingsFromServerAsync();
        Assert.False(online.SettingsAreDefaultFallback);
    }
}