// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>The client half of username + password sign-in: requests, how the server's answers are
/// told apart, and that only the session token is ever kept.</summary>
public class PasswordSignInStateTests
{
    private static (PasswordSignInState State, ScriptedServer Server) Create()
    {
        var server = new ScriptedServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        return (new PasswordSignInState(http), server);
    }

    internal static HttpResponseMessage Problem(HttpStatusCode status, string type) =>
        new(status) { Content = JsonContent.Create(new { type, title = type, status = (int)status }) };

    [Fact]
    public async Task IsOffered_FollowsTheServer_AndIsFalseWhenItCannotBeAsked()
    {
        var (state, server) = Create();
        Assert.False(await state.IsOfferedAsync()); // 404: an older server without the endpoint

        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = true });
        Assert.True(await state.IsOfferedAsync());

        server.On("GET api/auth/methods", _ => throw new HttpRequestException("offline"));
        Assert.False(await state.IsOfferedAsync());
    }

    [Fact]
    public async Task SignIn_ReturnsTheSessionToken_AndTheChangeRequiredFlag()
    {
        var (state, server) = Create();
        server.OnJson("POST api/auth/password/login", new PasswordLoginResponseDto { Token = "tok", PasswordChangeRequired = true });

        var attempt = await state.SignInAsync("alex", "violet harbour lantern 7");

        Assert.True(attempt.Succeeded);
        Assert.Equal("tok", attempt.Token);
        Assert.True(attempt.ChangeRequired);
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("alex", body["username"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PasswordFailure.InvalidCredentials)]
    [InlineData(HttpStatusCode.TooManyRequests, PasswordFailure.TooManyAttempts)]
    [InlineData(HttpStatusCode.InternalServerError, PasswordFailure.Other)]
    public async Task SignIn_PlainStatusCodes(HttpStatusCode status, PasswordFailure expected)
    {
        var (state, server) = Create();
        server.On("POST api/auth/password/login", _ => new HttpResponseMessage(status));
        Assert.Equal(expected, (await state.SignInAsync("a", "b")).Failure);
    }

    [Fact]
    public async Task Register_TellsSetupCodeGateAndRuleViolationsApart()
    {
        var (state, server) = Create();
        server.On("POST api/auth/password/register", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Equal(PasswordFailure.SetupSecretInvalid, (await state.RegisterAsync("A", "a", "p", " CODE ", null)).Failure);
        Assert.Equal("CODE", JsonNode.Parse(server.Bodies[^1])!["setupSecret"]!.GetValue<string>());

        server.OnJson("POST api/auth/password/register", new RegistrationGateErrorDto { Reason = "invite_required" }, HttpStatusCode.Forbidden);
        var gate = await state.RegisterAsync("A", "a", "p", null, null);
        Assert.Equal(PasswordFailure.RegistrationGate, gate.Failure);
        Assert.Equal("invite_required", gate.GateReason);

        server.On("POST api/auth/password/register", _ => Problem(HttpStatusCode.Conflict, "username-taken"));
        Assert.Equal(PasswordFailure.UsernameTaken, (await state.RegisterAsync("A", "a", "p", null, null)).Failure);

        server.On("POST api/auth/password/register", _ => Problem(HttpStatusCode.Forbidden, "password-sign-in-disabled"));
        Assert.Equal(PasswordFailure.Disabled, (await state.RegisterAsync("A", "a", "p", null, null)).Failure);
    }

    [Theory]
    [InlineData("username-invalid", PasswordFailure.UsernameInvalid)]
    [InlineData("username-fixed", PasswordFailure.UsernameFixed)]
    [InlineData("password-too-short", PasswordFailure.TooShort)]
    [InlineData("password-too-long", PasswordFailure.TooLong)]
    [InlineData("password-common", PasswordFailure.Common)]
    [InlineData("password-contains-username", PasswordFailure.ContainsUsername)]
    [InlineData("last-sign-in-method", PasswordFailure.LastMethod)]
    [InlineData("reauthentication-required", PasswordFailure.ProofRequired)]
    [InlineData("reauthentication-failed", PasswordFailure.ProofRejected)]
    [InlineData("owner-only", PasswordFailure.OwnerOnly)]
    public async Task ProblemTypes_AreMapped(string type, PasswordFailure expected)
    {
        var (state, server) = Create();
        server.On("PUT api/auth/password", _ => Problem(HttpStatusCode.BadRequest, type));
        Assert.Equal(expected, (await state.SetAsync("alex", "x", new PasswordProof())).Failure);
    }

    [Fact]
    public async Task Set_SendsTheNewPasswordWithTheProof()
    {
        var (state, server) = Create();
        server.On("PUT api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var attempt = await state.SetAsync(" alex ", "violet harbour lantern 7", new PasswordProof(CurrentPassword: "old one", RecoveryCode: " ABCD-EFGH-JKLM "));

        Assert.True(attempt.Succeeded);
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("alex", body["username"]!.GetValue<string>());
        Assert.Equal("violet harbour lantern 7", body["newPassword"]!.GetValue<string>());
        Assert.Equal("old one", body["currentPassword"]!.GetValue<string>());
        Assert.Equal("ABCD-EFGH-JKLM", body["recoveryCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Remove_And_Reset_CarryThePasskeyProof()
    {
        var (state, server) = Create();
        server.On("DELETE api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        server.OnJson("POST api/auth/users/4/password-reset", new PasswordResetResultDto { Username = "guest", TemporaryPassword = "abcde-fghjk-mnpqr-stuvw" });
        var proof = new PasswordProof(OptionsId: "opt", AssertionJson: "{\"id\":\"x\"}");

        Assert.True((await state.RemoveAsync(proof)).Succeeded);
        var reset = await state.ResetUserAsync(4, "guest", proof);

        Assert.Equal("abcde-fghjk-mnpqr-stuvw", reset.Reset!.TemporaryPassword);
        Assert.All(server.Bodies, b => Assert.Equal("opt", JsonNode.Parse(b)!["optionsId"]!.GetValue<string>()));
        Assert.Equal("guest", JsonNode.Parse(server.Bodies[^1])!["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnreachableServer_IsANetworkFailure()
    {
        var (state, server) = Create();
        server.On("POST api/auth/password/login", _ => throw new HttpRequestException("offline"));
        server.On("PUT api/auth/password", _ => throw new HttpRequestException("offline"));
        server.On("POST api/auth/users/1/password-reset", _ => throw new HttpRequestException("offline"));
        server.On("GET api/auth/password", _ => throw new HttpRequestException("offline"));

        Assert.Equal(PasswordFailure.Network, (await state.SignInAsync("a", "b")).Failure);
        Assert.Equal(PasswordFailure.Network, (await state.SetAsync(null, "b", new PasswordProof())).Failure);
        Assert.Equal(PasswordFailure.Network, (await state.ResetUserAsync(1, null, new PasswordProof())).Failure);
        Assert.Null(await state.GetStatusAsync());
    }

    [Fact]
    public void EveryFailure_HasAMessage()
    {
        var texts = PasswordTexts.Keys();
        foreach (var failure in Enum.GetValues<PasswordFailure>())
            Assert.False(string.IsNullOrEmpty(PasswordSignInState.Message(failure, texts)), failure.ToString());
    }
}

internal static class PasswordTexts
{
    /// <summary>The key names as texts, so every assertion also pins which key the markup uses.</summary>
    public static StudyLife.Client.I18nText.PasswordText Keys()
    {
        var texts = new StudyLife.Client.I18nText.PasswordText();
        foreach (var field in typeof(StudyLife.Client.I18nText.PasswordText).GetFields()
                     .Where(f => f.FieldType == typeof(string) && !f.IsStatic))
            field.SetValue(texts, field.Name);
        texts.CardUsernameFormat = "CardUsernameFormat:{0}";
        texts.CardUpdatedFormat = "CardUpdatedFormat:{0}";
        texts.AddedInfo = "AddedInfo:{0}";
        texts.ResetAriaFormat = "ResetAriaFormat:{0}";
        texts.ResetResultFormat = "ResetResultFormat:{0}:{1}";
        return texts;
    }
}

/// <summary>The password card in Setup.</summary>
public class SetupPasswordCardTests : ConsentBunitTest
{
    private sealed class WebOnly : INativeAppAuth
    {
        public bool IsAvailable => false;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
    }

    private (IRenderedComponent<SetupPasswordCard> Cut, ScriptedServer Server) Render(PasswordStatusDto status, List<UserListItemDto>? users = null)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/password", status);
        if (users is not null) server.OnJson("GET api/auth/users", users);
        else server.On("GET api/auth/users", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new AppStateService(http, js, NullLogger<AppStateService>.Instance, tokens));
        Services.AddSingleton<INativeAppAuth>(new WebOnly());
        var cut = Render<SetupPasswordCard>(p => p.Add(c => c.Text, PasswordTexts.Keys()));
        cut.WaitForState(() => cut.Markup.Contains("CardHeading") || server.Count("GET api/auth/password") > 0);
        return (cut, server);
    }

    [Fact]
    public void SwitchedOff_WithoutAPassword_RendersNothing()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = false, HasPassword = false });
        Assert.DoesNotContain("CardHeading", cut.Markup);
    }

    [Fact]
    public void AnExistingAccountWithoutPassword_IsOfferedToAddOne()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = false, HasPasskey = true });
        cut.WaitForAssertion(() => Assert.Contains("CardNoPassword", cut.Markup));
        Assert.Equal("AddButton", cut.Find("button.btn-secondary").TextContent.Trim());
        Assert.DoesNotContain("RemoveButton", cut.Markup);
    }

    [Fact]
    public void AddingAPassword_SendsNameAndPassword_WithTheRecoveryCodeAsProof()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = false });
        server.On("PUT api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        cut.WaitForAssertion(() => cut.Find("button.btn-secondary"));

        cut.Find("button.btn-secondary").Click();
        Assert.Equal("username", cut.Find("#password-username").GetAttribute("autocomplete"));
        Assert.Equal("new-password", cut.Find("#password-new").GetAttribute("autocomplete"));
        cut.Find("#password-username").Input("alex");
        cut.Find("#password-new").Input("violet harbour lantern 7");
        cut.Find("#password-recovery").Input("ABCD-EFGH-JKLM");
        server.OnJson("GET api/auth/password", new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex" });
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("AddedInfo:alex", cut.Markup));
        var body = JsonNode.Parse(server.Bodies.Single(b => b.Contains("newPassword")))!.AsObject();
        Assert.Equal("alex", body["username"]!.GetValue<string>());
        Assert.Equal("ABCD-EFGH-JKLM", body["recoveryCode"]!.GetValue<string>());
    }

    [Fact]
    public void ChangeMode_ShowsTheUsernameReadOnly()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true });
        cut.WaitForAssertion(() => Assert.Contains("ChangeButton", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "ChangeButton").Click();
        var field = cut.Find("#password-username");
        Assert.True(field.HasAttribute("readonly"));
        Assert.Equal("alex", field.GetAttribute("value"));
        Assert.Contains("UsernameFixedHint", cut.Markup);
    }

    [Fact]
    public void AddMode_LetsYouChooseTheUsername()
    {
        var (addCut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = false });
        addCut.WaitForAssertion(() => Assert.Contains("AddButton", addCut.Markup));
        addCut.FindAll("button").First(b => b.TextContent.Trim() == "AddButton").Click();
        Assert.False(addCut.Find("#password-username").HasAttribute("readonly"));
    }

    [Fact]
    public void ARefusedRename_ShowsTheFixedNameReason()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true });
        server.On("PUT api/auth/password", _ => PasswordSignInStateTests.Problem(HttpStatusCode.BadRequest, "username-fixed"));
        cut.WaitForAssertion(() => Assert.Contains("ChangeButton", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "ChangeButton").Click();
        cut.Find("#password-new").Input("password1234");
        cut.Find("#password-current").Input("old phrase here!");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("ErrorUsernameFixed", cut.Find("[role=alert]").TextContent.Trim()));
    }

    [Fact]
    public void ARefusedPassword_ShowsTheReason()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true });
        server.On("PUT api/auth/password", _ => PasswordSignInStateTests.Problem(HttpStatusCode.BadRequest, "password-common"));
        cut.WaitForAssertion(() => Assert.Contains("ChangeButton", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "ChangeButton").Click();
        Assert.Equal("current-password", cut.Find("#password-current").GetAttribute("autocomplete"));
        cut.Find("#password-new").Input("password1234");
        cut.Find("#password-current").Input("old phrase here!");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("ErrorCommon", cut.Find("[role=alert]").TextContent.Trim()));
    }

    [Fact]
    public void TheOnlyMethod_CannotBeRemoved_AndATemporaryPasswordAsksForAChange()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = false, ChangeRequired = true });
        cut.WaitForAssertion(() => Assert.Contains("CardChangeRequired", cut.Markup));
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Trim() == "RemoveButton").HasAttribute("disabled"));
        Assert.Contains("RemoveBlockedHint", cut.Markup);
        Assert.Contains("CardUsernameFormat:alex", cut.Markup);
    }

    [Fact]
    public void TheOwner_ResetsAnotherPassword_AndSeesItOnce()
    {
        var (cut, server) = Render(
            new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "owner", CanRemove = true },
            [new UserListItemDto { Id = 4, DisplayName = "Guest", Username = null }]);
        server.OnJson("POST api/auth/users/4/password-reset", new PasswordResetResultDto { Username = "guest", TemporaryPassword = "abcde-fghjk-mnpqr-stuvw" });
        cut.WaitForAssertion(() => Assert.Contains("ResetHeading", cut.Markup));

        var reset = cut.Find("[role=listitem] button");
        Assert.Equal("ResetAriaFormat:Guest", reset.GetAttribute("aria-label"));
        reset.Click();
        cut.Find("#password-username").Input("guest");
        cut.Find("#password-current").Input("owner phrase here");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("abcde-fghjk-mnpqr-stuvw", cut.Markup));
        Assert.Contains("ResetResultFormat:Guest:guest", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "DoneButton").Click();
        Assert.DoesNotContain("abcde-fghjk-mnpqr-stuvw", cut.Markup);
    }
}

/// <summary>The sign-in form under the passkey button.</summary>
public class PasswordSignInFormTests : ConsentBunitTest
{
    private (IRenderedComponent<PasswordSignInForm> Cut, ScriptedServer Server, SessionTokenStore Tokens) Render(bool offered)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = offered });
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(tokens);
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<PasswordSignInForm>(p => p.Add(c => c.Text, PasswordTexts.Keys()));
        cut.WaitForState(() => server.Count("GET api/auth/methods") > 0);
        return (cut, server, tokens);
    }

    [Fact]
    public void SwitchedOff_ShowsNothing()
    {
        var (cut, _, _) = Render(offered: false);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void TheForm_IsReadyForPasswordManagers()
    {
        var (cut, _, _) = Render(offered: true);
        cut.WaitForAssertion(() => cut.Find("form"));
        Assert.Equal("username", cut.Find("#signin-username").GetAttribute("autocomplete"));
        Assert.Equal("current-password", cut.Find("#signin-password").GetAttribute("autocomplete"));
        Assert.Equal("password", cut.Find("#signin-password").GetAttribute("type"));
        Assert.Equal("signin-username", cut.Find("label[for=signin-username]").GetAttribute("for"));
    }

    [Fact]
    public void ASuccessfulSignIn_KeepsOnlyTheToken_AndATemporaryPasswordGoesToSettings()
    {
        var (cut, server, tokens) = Render(offered: true);
        server.OnJson("POST api/auth/password/login", new PasswordLoginResponseDto { Token = "session-token", PasswordChangeRequired = true });
        cut.WaitForAssertion(() => cut.Find("form"));

        cut.Find("#signin-username").Input("alex");
        cut.Find("#signin-password").Input("violet harbour lantern 7");
        cut.Find("form").Submit();

        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/setup#password", nav.Uri));
        Assert.Equal("session-token", tokens.Token);
    }

    [Fact]
    public void AWrongPassword_ShowsTheGenericMessage_AndClearsTheField()
    {
        var (cut, server, tokens) = Render(offered: true);
        server.On("POST api/auth/password/login", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        cut.WaitForAssertion(() => cut.Find("form"));

        cut.Find("#signin-username").Input("alex");
        cut.Find("#signin-password").Input("wrong guess");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("LoginFailed", cut.Find("[role=alert]").TextContent.Trim()));
        Assert.Equal("", cut.Find("#signin-password").GetAttribute("value") ?? "");
        Assert.Null(tokens.Token);
    }
}

/// <summary>The account-deletion dialog accepts the current password as its proof.</summary>
public class DeleteAccountPasswordProofTests : ConsentBunitTest
{
    private sealed class WebOnly : INativeAppAuth
    {
        public bool IsAvailable => false;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
    }

    private static StudyLife.Client.I18nText.AccountDeletionText Keys()
    {
        var texts = new StudyLife.Client.I18nText.AccountDeletionText();
        foreach (var field in typeof(StudyLife.Client.I18nText.AccountDeletionText).GetFields()
                     .Where(f => f.FieldType == typeof(string) && !f.IsStatic))
            field.SetValue(texts, field.Name);
        return texts;
    }

    private (IRenderedComponent<SetupDeleteAccountCard> Cut, ScriptedServer Server) Render(PasswordStatusDto? status)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/account/deletion-preview", new AccountDeletionPreviewDto { Available = true });
        if (status is not null) server.OnJson("GET api/auth/password", status);
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(http);
        Services.AddSingleton(tokens);
        Services.AddSingleton(new AppStateService(http, js, NullLogger<AppStateService>.Instance, tokens));
        Services.AddSingleton<INativeAppAuth>(new WebOnly());
        Services.AddSingleton<INativeFileExport>(new NoNativeFileExport());
        var cut = Render<SetupDeleteAccountCard>(p => p.Add(c => c.Text, Keys()));
        cut.WaitForState(() => cut.Markup.Contains("OpenButton"));
        return (cut, server);
    }

    private static void ToReauth(IRenderedComponent<SetupDeleteAccountCard> cut)
    {
        cut.Find("button.btn-danger").Click();
        cut.Find("input[type=checkbox]").Change(true);
        cut.Find(".modal-footer button.btn-danger").Click();
    }

    [Fact]
    public void WithoutAPassword_ThePasswordOptionIsNotOffered()
    {
        var (cut, _) = Render(null);
        ToReauth(cut);
        Assert.DoesNotContain("PasswordToggle", cut.Markup);
    }

    [Fact]
    public void WithAPassword_ItCanConfirmTheDeletion()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex" });
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto());
        cut.WaitForState(() => server.Count("GET api/auth/password") > 0);

        ToReauth(cut);
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "PasswordToggle").Click();
        Assert.Equal("current-password", cut.Find("#delete-account-password").GetAttribute("autocomplete"));
        cut.Find("#delete-account-password").Input("violet harbour lantern 7");
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "PasswordButton").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("DELETE api/auth/account")));
        var body = JsonNode.Parse(server.Bodies.Single())!.AsObject();
        Assert.Equal("violet harbour lantern 7", body["currentPassword"]!.GetValue<string>());
    }
}
