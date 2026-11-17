// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Components;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The client half of the server's step-up (2026-11-16 audit, M1): the proof request, how its
/// outcomes are told apart and how a refused guarded request is recognised.</summary>
public class StepUpStateTests
{
    private static (StepUpState State, ScriptedServer Server) Create()
    {
        var server = new ScriptedServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        return (new StepUpState(http), server);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string type) =>
        new(status) { Content = JsonContent.Create(new { type, title = type, status = (int)status }) };

    [Fact]
    public async Task Confirm_SendsTheProofAsTheServersFreshProofBody()
    {
        var (state, server) = Create();
        server.On("POST api/auth/account/reauth", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var outcome = await state.ConfirmAsync(new FreshProof(RecoveryCode: " ABCD-EFGH-JKLM "));

        Assert.Equal(StepUpOutcome.Confirmed, outcome);
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("ABCD-EFGH-JKLM", body["recoveryCode"]!.GetValue<string>());
        Assert.False(body.ContainsKey("currentPassword"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, StepUpOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, StepUpOutcome.Missing)]
    [InlineData(HttpStatusCode.TooManyRequests, StepUpOutcome.TooManyAttempts)]
    [InlineData(HttpStatusCode.InternalServerError, StepUpOutcome.Other)]
    public async Task Refusals_AreToldApart(HttpStatusCode status, StepUpOutcome expected)
    {
        var (state, server) = Create();
        server.On("POST api/auth/account/reauth", _ => new HttpResponseMessage(status));

        Assert.Equal(expected, await state.ConfirmAsync(new FreshProof(CurrentPassword: "pw")));
    }

    [Fact]
    public async Task OnlyTheStepUpProblem_CountsAsStepUpRequired()
    {
        Assert.True(await StepUpState.IsStepUpRequiredAsync(Problem(HttpStatusCode.Forbidden, "step-up-required")));
        Assert.False(await StepUpState.IsStepUpRequiredAsync(Problem(HttpStatusCode.Forbidden, "approval-needs-other-session")));
        Assert.False(await StepUpState.IsStepUpRequiredAsync(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.False(await StepUpState.IsStepUpRequiredAsync(new HttpResponseMessage(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task TheProblemType_CanBeReadTwice()
    {
        var response = Problem(HttpStatusCode.Forbidden, "approval-needs-other-session");

        Assert.False(await StepUpState.IsStepUpRequiredAsync(response));
        Assert.Equal(StepUpState.ApprovalNeedsOtherSessionType, await StepUpState.ProblemTypeAsync(response));
    }
}

/// <summary>The dialog: offers the proofs, confirms through the server and hands control back.</summary>
public class StepUpDialogTests : ConsentBunitTest
{
    private sealed class NoNativeAuth : INativeAppAuth
    {
        public bool IsAvailable => false;
        public bool SupportsInAppPasskeys => false;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
        public Task AccountDeletedAsync() => Task.CompletedTask;
    }

    private static StudyLife.Client.I18nText.StepUpText KeyTexts()
    {
        var texts = new StudyLife.Client.I18nText.StepUpText();
        foreach (var field in typeof(StudyLife.Client.I18nText.StepUpText).GetFields()
                     .Where(f => f.FieldType == typeof(string) && !f.IsStatic))
            field.SetValue(texts, field.Name);
        return texts;
    }

    private (IRenderedComponent<StepUpDialog> Cut, ScriptedServer Server, Func<int> Confirmed) Render()
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/password", new PasswordStatusDto { Enabled = true, HasPasskey = true, HasPassword = true });
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(http);
        Services.AddSingleton(tokens);
        Services.AddSingleton(new AppStateService(http, js, NullLogger<AppStateService>.Instance, tokens));
        Services.AddSingleton<INativeAppAuth>(new NoNativeAuth());
        Services.AddI18nText();
        var confirmed = 0;
        var cut = Render<StepUpDialog>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.Text, KeyTexts())
            .Add(c => c.OnConfirmed, () => confirmed++));
        cut.WaitForAssertion(() => Assert.Contains("PasswordToggle", cut.Markup));
        return (cut, server, () => confirmed);
    }

    [Fact]
    public void APassword_ConfirmsTheSession_AndHandsBackToTheHost()
    {
        var (cut, server, confirmed) = Render();
        server.On("POST api/auth/account/reauth", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "PasswordToggle").Click();
        cut.Find("#step-up-password").Input("correct horse");
        cut.FindAll(".modal-footer button").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal(1, confirmed()));
        Assert.Contains("correct horse", Assert.Single(server.Bodies));
    }

    [Fact]
    public void ARejectedProof_ShowsAnAnnouncedError_AndDoesNotHandBack()
    {
        var (cut, server, confirmed) = Render();
        server.On("POST api/auth/account/reauth", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#step-up-recovery").Input("AAAA-BBBB-CCCC");
        cut.FindAll(".modal-footer button").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal("ErrorRejected", cut.Find("[role=alert]").TextContent.Trim()));
        Assert.Equal(0, confirmed());
    }
}
