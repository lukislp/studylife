// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using StudyLife.Client.Services;
using static StudyLife.Client.Tests.AiConsentFixtures;

namespace StudyLife.Client.Tests;

/// <summary>
/// "Ensure consent, then continue": the one question every AI entry point asks. The rule itself
/// is the server's (AiConsentService); these tests pin how the answer turns into the dialog flow,
/// without any markup.
/// </summary>
public class AiConsentStateTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private static async Task<AiConsentRequest> WaitForDialogAsync(AiConsentState state)
    {
        // The dialog is raised synchronously once the status answer arrived; poll the state, not a clock.
        var deadline = DateTime.UtcNow + SafetyNet;
        while (state.Pending is null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the consent dialog never appeared");
            await Task.Yield();
        }
        return state.Pending;
    }

    // ── no dialog needed ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, false)] // no add-on at all
    [InlineData(true, false, false)] // local-only provider
    [InlineData(true, true, true)]   // a stored consent covers the provider
    public async Task EnsureConsent_WhenNothingIsToBeAsked_ContinuesWithoutADialog(bool available, bool required, bool granted)
    {
        var server = new ScriptedServer().OnJson("GET api/ai/consent", Status(available, required, granted));
        var state = NewState(server);

        Assert.True(await state.EnsureConsentAsync());

        Assert.Null(state.Pending);
        Assert.Equal(0, server.Count("POST api/ai/consent/grant"));
    }

    [Fact]
    public async Task EnsureConsent_WhenTheServerCannotBeAsked_DoesNotContinue()
    {
        var state = NewState(new ScriptedServer()); // 404 for the status

        Assert.False(await state.EnsureConsentAsync());
    }

    // ── agree / not now ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureConsent_WhenRequired_AsksAndRecordsTheConsentForTheShownDescription()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", Status(fingerprint: 'b'))
            .OnJson("POST api/ai/consent/grant", Status(granted: true, fingerprint: 'b'));
        var state = NewState(server);

        var ensure = state.EnsureConsentAsync();
        var request = await WaitForDialogAsync(state);
        Assert.False(request.ExistingUser);
        Assert.Equal(Fingerprint('b'), request.Status.Fingerprint);
        Assert.True(await state.AgreeAsync());

        Assert.True(await ensure);
        Assert.Null(state.Pending);
        Assert.Contains(Fingerprint('b'), Assert.Single(server.Bodies)); // the consent binds to what was shown
    }

    [Fact]
    public async Task EnsureConsent_NotNow_DoesNotContinue_AndChangesNothing()
    {
        var server = new ScriptedServer().OnJson("GET api/ai/consent", Status());
        var state = NewState(server);

        var ensure = state.EnsureConsentAsync();
        await WaitForDialogAsync(state);
        Assert.True(await state.DeclineAsync());

        Assert.False(await ensure); // declining is an answer, not an error
        Assert.Null(state.Pending);
        Assert.Equal(0, server.Count("POST api/ai/consent/grant"));
        Assert.Equal(0, server.Count("POST api/ai/consent/withdraw")); // a new user has nothing to revoke
    }

    [Fact]
    public async Task Agree_WhenTheServerFails_KeepsTheDialogOpen()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", Status())
            .On("POST api/ai/consent/grant", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var state = NewState(server);

        var ensure = state.EnsureConsentAsync();
        await WaitForDialogAsync(state);

        Assert.False(await state.AgreeAsync());
        Assert.NotNull(state.Pending); // the user can try again

        await state.DeclineAsync();
        Assert.False(await ensure);
    }

    [Fact]
    public async Task Agree_WhenTheProviderChangedMeanwhile_ShowsTheNewDescriptionAndAsksAgain()
    {
        var server = new ScriptedServer();
        var statusCalls = 0;
        server.On("GET api/ai/consent", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(Status(fingerprint: ++statusCalls == 1 ? 'a' : 'c')),
        });
        server.OnJson("POST api/ai/consent/grant", Status(fingerprint: 'c'), HttpStatusCode.Conflict);
        var state = NewState(server);

        var ensure = state.EnsureConsentAsync();
        var first = await WaitForDialogAsync(state);
        Assert.False(first.ProviderChanged);
        await state.AgreeAsync(); // 409: nothing was recorded

        var second = await WaitForDialogAsync(state);
        Assert.NotSame(first, second);
        Assert.True(second.ProviderChanged);
        Assert.Equal(Fingerprint('c'), second.Status.Fingerprint);

        server.OnJson("POST api/ai/consent/grant", Status(granted: true, fingerprint: 'c'));
        await state.AgreeAsync();
        Assert.True(await ensure);
    }

    // ── app start (existing AI users) ────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAtStartup_WithoutADecisionToMake_AsksNothing_AndReportsAvailability()
    {
        var server = new ScriptedServer().OnJson("GET api/ai/consent", Status(granted: true, hasKey: true));
        var state = NewState(server);

        Assert.True(await state.CheckAtStartupAsync());
        Assert.Null(state.Pending);
    }

    [Fact]
    public async Task CheckAtStartup_ExistingUserWithoutConsent_IsAsked_AndDecliningRevokesTheKey()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", Status(hasKey: true, needsDecision: true))
            .On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var state = NewState(server);

        var check = state.CheckAtStartupAsync();
        var request = await WaitForDialogAsync(state);
        Assert.True(request.ExistingUser);
        Assert.True(await state.DeclineAsync());
        await check;

        Assert.Equal(1, server.Count("POST api/ai/consent/withdraw")); // key revoked, data purged server side
        Assert.Null(state.Pending);
    }

    [Fact]
    public async Task CheckAtStartup_ExistingUserAgreeing_KeepsAiOn_WithoutWithdrawing()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", Status(hasKey: true, needsDecision: true))
            .OnJson("POST api/ai/consent/grant", Status(granted: true, hasKey: true));
        var state = NewState(server);

        var check = state.CheckAtStartupAsync();
        await WaitForDialogAsync(state);
        await state.AgreeAsync();
        await check;

        Assert.Equal(0, server.Count("POST api/ai/consent/withdraw"));
    }

    [Fact]
    public async Task Decline_ForAnExistingUser_WhenTheRevokeFails_KeepsTheDialogOpen()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", Status(hasKey: true, needsDecision: true))
            .On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var state = NewState(server);

        var check = state.CheckAtStartupAsync();
        await WaitForDialogAsync(state);

        Assert.False(await state.DeclineAsync()); // never pretend the data was removed
        Assert.NotNull(state.Pending);

        server.On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.True(await state.DeclineAsync());
        await check;
    }

    // ── withdraw and the 403 problem ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_CallsTheEndpoint_AndRefreshesTheStatus()
    {
        var server = new ScriptedServer()
            .On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .OnJson("GET api/ai/consent", Status());
        var state = NewState(server);

        Assert.True(await state.WithdrawAsync());

        Assert.Equal(1, server.Count("GET api/ai/consent"));
        Assert.False((await state.GetStatusAsync())!.Granted);
    }

    [Fact]
    public async Task Withdraw_WhenTheServerFails_ReportsFailure()
    {
        var server = new ScriptedServer().On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.False(await NewState(server).WithdrawAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"type\":\"ai-consent-required\",\"status\":403}", true)]
    [InlineData(HttpStatusCode.Forbidden, "{\"type\":\"something-else\",\"status\":403}", false)]
    [InlineData(HttpStatusCode.Forbidden, "not json", false)]
    [InlineData(HttpStatusCode.Forbidden, "", false)]
    [InlineData(HttpStatusCode.OK, "{\"type\":\"ai-consent-required\"}", false)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"type\":\"ai-consent-required\"}", false)]
    public async Task IsConsentRequired_RecognisesOnlyTheServersConsentProblem(HttpStatusCode status, string body, bool expected)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(body) };

        Assert.Equal(expected, await AiConsentState.IsConsentRequiredAsync(response));
    }

    private static HttpContent JsonContent(object value) => System.Net.Http.Json.JsonContent.Create(value);
}
