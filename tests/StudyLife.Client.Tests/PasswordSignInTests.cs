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
using Toolbelt.Blazor.Extensions.DependencyInjection;

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
    public async Task GetOffer_TellsAnAnswerFromNotBeingAbleToAsk()
    {
        var (state, server) = Create();
        Assert.Equal(PasswordOffer.NotOffered, await state.GetOfferAsync()); // 404: an older server without the endpoint

        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = true });
        Assert.Equal(PasswordOffer.Offered, await state.GetOfferAsync());

        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = false });
        Assert.Equal(PasswordOffer.NotOffered, await state.GetOfferAsync()); // 200 false: the server said no

        server.On("GET api/auth/methods", _ => throw new HttpRequestException("offline"));
        Assert.Equal(PasswordOffer.Unknown, await state.GetOfferAsync());

        server.On("GET api/auth/methods", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(PasswordOffer.Unknown, await state.GetOfferAsync());

        server.On("GET api/auth/methods", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>portal</html>") });
        Assert.Equal(PasswordOffer.Unknown, await state.GetOfferAsync());
    }

    [Fact]
    public async Task SignIn_ReturnsTheSessionToken()
    {
        var (state, server) = Create();
        server.OnJson("POST api/auth/password/login", new PasswordLoginResponseDto { Token = "tok" });

        var attempt = await state.SignInAsync("alex", "violet harbour lantern 7");

        Assert.True(attempt.Succeeded);
        Assert.Equal("tok", attempt.Token);
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
    public async Task Remove_CarriesThePasskeyProof()
    {
        var (state, server) = Create();
        server.On("DELETE api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var proof = new PasswordProof(OptionsId: "opt", AssertionJson: "{\"id\":\"x\"}");

        Assert.True((await state.RemoveAsync(proof)).Succeeded);

        Assert.Equal("opt", JsonNode.Parse(Assert.Single(server.Bodies))!["optionsId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnreachableServer_IsANetworkFailure()
    {
        var (state, server) = Create();
        server.On("POST api/auth/password/login", _ => throw new HttpRequestException("offline"));
        server.On("PUT api/auth/password", _ => throw new HttpRequestException("offline"));
        server.On("GET api/auth/password", _ => throw new HttpRequestException("offline"));

        Assert.Equal(PasswordFailure.Network, (await state.SignInAsync("a", "b")).Failure);
        Assert.Equal(PasswordFailure.Network, (await state.SetAsync(null, "b", new PasswordProof())).Failure);
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

    private sealed class FakeNative : INativeAppAuth
    {
        public bool IsAvailable => true;
        public bool SupportsInAppPasskeys { get; init; }
        public string? SignInToken;
        public int AuthenticateCalls;
        public string? LastPage;
        public Task<string?> AuthenticateAsync(string startPage)
        {
            AuthenticateCalls++;
            LastPage = startPage;
            return Task.FromResult(SignInToken);
        }
    }

    private static void ConfirmWith(IRenderedComponent<SetupPasswordCard> cut, string toggle, string inputId, string value, string confirm)
    {
        cut.FindAll("button").First(b => b.TextContent.Trim() == toggle).Click();
        cut.Find("#" + inputId).Input(value);
        cut.FindAll("button").First(b => b.TextContent.Trim() == confirm).Click();
    }

    private (IRenderedComponent<SetupPasswordCard> Cut, ScriptedServer Server) Render(PasswordStatusDto status, INativeAppAuth? native = null, bool startChange = false)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/password", status);
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(tokens);
        Services.AddSingleton(new AppStateService(http, js, NullLogger<AppStateService>.Instance, tokens));
        Services.AddSingleton(native ?? new WebOnly());
        var cut = Render<SetupPasswordCard>(p => p.Add(c => c.Text, PasswordTexts.Keys()).Add(c => c.StartChange, startChange));
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
        server.OnJson("GET api/auth/password", new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex" });
        cut.FindAll("button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#password-recovery").Input("ABCD-EFGH-JKLM");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "SaveButton").Click();

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
        ConfirmWith(cut, "PasswordToggle", "password-current", "old phrase here!", "SaveButton");
        cut.WaitForAssertion(() => Assert.Equal("ErrorUsernameFixed", cut.Find("[role=alert]").TextContent.Trim()));
    }

    [Fact]
    public void ARefusedPassword_ShowsTheReason()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true });
        server.On("PUT api/auth/password", _ => PasswordSignInStateTests.Problem(HttpStatusCode.BadRequest, "password-common"));
        cut.WaitForAssertion(() => Assert.Contains("ChangeButton", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "ChangeButton").Click();
        cut.Find("#password-new").Input("password1234");
        ConfirmWith(cut, "PasswordToggle", "password-current", "old phrase here!", "SaveButton");

        cut.WaitForAssertion(() => Assert.Equal("ErrorCommon", cut.Find("[role=alert]").TextContent.Trim()));
    }

    private static PasswordStatusDto PasskeyOnly() => new() { Enabled = true, HasPassword = false, HasPasskey = true };

    private static void StartAdding(IRenderedComponent<SetupPasswordCard> cut)
    {
        cut.WaitForAssertion(() => Assert.Contains("AddButton", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "AddButton").Click();
        cut.Find("#password-username").Input("alex");
        cut.Find("#password-new").Input("violet harbour lantern 7");
    }

    private static List<string> ButtonLabels(IRenderedComponent<SetupPasswordCard> cut) =>
        cut.FindAll("form button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void NativeWithoutInAppPasskeys_OffersConfirmBySigningIn_NotThePasskeyPrompt()
    {
        var (cut, _) = Render(PasskeyOnly(), native: new FakeNative { SignInToken = "fresh" });
        StartAdding(cut);

        var labels = ButtonLabels(cut);
        Assert.DoesNotContain("ConfirmWithPasskey", labels);
        Assert.DoesNotContain("PasswordToggle", labels); // the account has no password
        Assert.Contains("RecoveryToggle", labels);
        Assert.Contains("BrowserSignInHint", cut.Markup);
        // The recommended route comes first, the recovery code after it.
        Assert.True(labels.IndexOf("BrowserSignInButton") >= 0 && labels.IndexOf("BrowserSignInButton") < labels.IndexOf("RecoveryToggle"));
    }

    [Fact]
    public void ConfirmBySigningIn_StoresTheFreshToken_AndSubmitsWithoutAnExplicitProof()
    {
        var native = new FakeNative { SignInToken = "fresh-token" };
        var (cut, server) = Render(PasskeyOnly(), native: native);
        server.On("PUT api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        StartAdding(cut);
        server.OnJson("GET api/auth/password", new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex" });

        cut.FindAll("button").First(b => b.TextContent.Trim() == "BrowserSignInButton").Click();

        cut.WaitForAssertion(() => Assert.Contains("AddedInfo:alex", cut.Markup));
        Assert.Equal(1, native.AuthenticateCalls);
        Assert.Equal("login", native.LastPage);
        Assert.Equal("fresh-token", ((IServiceProvider)Ctx.Services).GetRequiredService<SessionTokenStore>().Token);
        var body = JsonNode.Parse(server.Bodies.Single(b => b.Contains("newPassword")))!.AsObject();
        Assert.False(body.ContainsKey("currentPassword"));
        Assert.False(body.ContainsKey("recoveryCode"));
        Assert.False(body.ContainsKey("optionsId"));
    }

    [Fact]
    public void ConfirmBySigningIn_WaitsUntilTheNewPasswordIsFilledIn()
    {
        var (cut, _) = Render(PasskeyOnly(), native: new FakeNative { SignInToken = "fresh" });
        cut.WaitForAssertion(() => Assert.Contains("AddButton", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "AddButton").Click();

        Assert.True(cut.FindAll("button").First(b => b.TextContent.Trim() == "BrowserSignInButton").HasAttribute("disabled"));
    }

    [Fact]
    public void ACancelledBrowserSignIn_SendsNothing_AndSaysSo()
    {
        var (cut, server) = Render(PasskeyOnly(), native: new FakeNative { SignInToken = null });
        StartAdding(cut);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "BrowserSignInButton").Click();

        cut.WaitForAssertion(() => Assert.Equal("ErrorProofRejected", cut.Find("[role=alert]").TextContent.Trim()));
        Assert.Equal(0, server.Count("PUT api/auth/password"));
    }

    [Fact]
    public void WhenTheServerStillWantsAProofAfterTheSignIn_TheMessageSaysTheSignInWasTooOld()
    {
        var (cut, server) = Render(PasskeyOnly(), native: new FakeNative { SignInToken = "fresh" });
        server.On("PUT api/auth/password", _ => PasswordSignInStateTests.Problem(HttpStatusCode.Forbidden, "reauthentication-required"));
        StartAdding(cut);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "BrowserSignInButton").Click();

        cut.WaitForAssertion(() => Assert.Equal("ErrorFreshSignInExpired", cut.Find("[role=alert]").TextContent.Trim()));
        // The typed password stays, so a second try needs only the sign-in again.
        Assert.Equal("violet harbour lantern 7", cut.Find("#password-new").GetAttribute("value"));
    }

    [Fact]
    public void NativeWithInAppPasskeys_KeepsThePasskeyPrompt()
    {
        var (cut, _) = Render(PasskeyOnly(), native: new FakeNative { SupportsInAppPasskeys = true });
        StartAdding(cut);

        var labels = ButtonLabels(cut);
        Assert.Contains("ConfirmWithPasskey", labels);
        Assert.DoesNotContain("BrowserSignInButton", labels);
    }

    [Fact]
    public void OnTheWeb_APasskeyAccountSeesThePasskeyButton()
    {
        var (cut, _) = Render(PasskeyOnly());
        StartAdding(cut);
        Assert.Contains("ConfirmWithPasskey", ButtonLabels(cut));
    }

    [Fact]
    public void OnTheWeb_WithoutAPasskeyOrPassword_OnlyTheRecoveryCodeIsOffered()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = false, HasPasskey = false });
        StartAdding(cut);
        var labels = ButtonLabels(cut);
        Assert.DoesNotContain("ConfirmWithPasskey", labels);
        Assert.Contains("RecoveryToggle", labels);
    }

    [Fact]
    public void TheOnlyMethod_CannotBeRemoved()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = false });
        cut.WaitForAssertion(() => Assert.Contains("CardUsernameFormat:alex", cut.Markup));
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Trim() == "RemoveButton").HasAttribute("disabled"));
        Assert.Contains("RemoveBlockedHint", cut.Markup);
    }

    [Fact]
    public void NoResetOfAnotherAccountIsOffered_NotEvenToTheOwner()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "owner", CanRemove = true });
        cut.WaitForAssertion(() => Assert.Contains("CardHeading", cut.Markup));
        Assert.DoesNotContain("Reset", cut.Markup);
        Assert.Equal(0, server.Count("GET api/auth/users"));
    }

    [Fact]
    public void FromTheForgotPath_TheChangeFormIsOpen_AndSavingNeedsNoExtraProof()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true },
            startChange: true);
        server.On("PUT api/auth/password", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        cut.WaitForAssertion(() => cut.Find("#password-new"));
        Assert.Contains("ForgotCardHint", cut.Markup);
        // The sign-in was the proof: no confirmation picker, just Save.
        Assert.DoesNotContain("PasswordToggle", cut.Markup);
        Assert.DoesNotContain("RecoveryToggle", cut.Markup);
        Assert.DoesNotContain("ConfirmWithPasskey", cut.Markup);

        cut.Find("#password-new").Input("quiet meadow compass 19");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "SaveButton").Click();

        cut.WaitForAssertion(() => Assert.Contains("ChangedInfo", cut.Markup));
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("quiet meadow compass 19", body["newPassword"]!.GetValue<string>());
        Assert.Null(body["currentPassword"]);
        Assert.Null(body["recoveryCode"]);
        Assert.Null(body["optionsId"]);
    }

    [Fact]
    public void FromTheForgotPath_AnExpiredSignInFallsBackToTheConfirmationPicker()
    {
        var (cut, server) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true, HasPasskey = true },
            startChange: true);
        server.On("PUT api/auth/password", _ => Problem(HttpStatusCode.BadRequest, "reauthentication-required"));
        cut.WaitForAssertion(() => cut.Find("#password-new"));

        cut.Find("#password-new").Input("quiet meadow compass 19");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "SaveButton").Click();

        cut.WaitForAssertion(() => Assert.Contains("ErrorFreshSignInExpired", cut.Markup));
        Assert.Contains("RecoveryToggle", cut.Markup);
    }

    [Fact]
    public void WithoutTheForgotFlag_TheCardStartsClosed()
    {
        var (cut, _) = Render(new PasswordStatusDto { Enabled = true, HasPassword = true, Username = "alex", CanRemove = true });
        cut.WaitForAssertion(() => Assert.Contains("ChangeButton", cut.Markup));
        Assert.Empty(cut.FindAll("#password-new"));
        Assert.DoesNotContain("ForgotCardHint", cut.Markup);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string type) =>
        new(status) { Content = new StringContent($"{{\"type\":\"{type}\"}}", System.Text.Encoding.UTF8, "application/json") };
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

    private sealed class FakeSignal : IConnectivitySignal
    {
        public event Action? Restored;
        public void Raise() => Restored?.Invoke();
    }

    private (IRenderedComponent<PasswordSignInForm> Cut, ScriptedServer Server, FakeSignal Signal) RenderWithSignal(Func<HttpRequestMessage, HttpResponseMessage> methods)
    {
        var server = new ScriptedServer().On("GET api/auth/methods", methods);
        var signal = new FakeSignal();
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(new StubJSRuntime()));
        Services.AddSingleton<IConnectivitySignal>(signal);
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<PasswordSignInForm>(p => p.Add(c => c.Text, PasswordTexts.Keys()));
        cut.WaitForState(() => server.Count("GET api/auth/methods") == 1);
        return (cut, server, signal);
    }

    [Fact]
    public void WhileTheServerCannotBeAsked_TheFormStaysHidden_AndAppearsWhenConnectivityReturns()
    {
        var (cut, server, signal) = RenderWithSignal(_ => throw new HttpRequestException("offline"));
        Assert.Empty(cut.FindAll("form")); // Unknown is not "not offered", and nothing was cached

        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = true });
        signal.Raise(); // the platform reports connectivity back: ask now, not at the next backoff step

        cut.WaitForAssertion(() => cut.Find("form"));
        Assert.Equal(2, server.Count("GET api/auth/methods"));
    }

    [Fact]
    public void ADefiniteNo_IsNotAskedAgain_EvenWhenConnectivityReturns()
    {
        var (cut, server, signal) = RenderWithSignal(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new AuthMethodsDto { PasswordSignIn = false }) });

        signal.Raise();

        Assert.Equal(1, server.Count("GET api/auth/methods"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void AfterDisposal_ConnectivityNoLongerTriggersQuestions()
    {
        var (cut, server, signal) = RenderWithSignal(_ => throw new HttpRequestException("offline"));

        cut.Dispose();
        signal.Raise();

        Assert.Equal(1, server.Count("GET api/auth/methods"));
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
    public void ASuccessfulSignIn_KeepsOnlyTheToken_AndGoesToTheDashboard()
    {
        var (cut, server, tokens) = Render(offered: true);
        server.OnJson("POST api/auth/password/login", new PasswordLoginResponseDto { Token = "session-token" });
        cut.WaitForAssertion(() => cut.Find("form"));

        cut.Find("#signin-username").Input("alex");
        cut.Find("#signin-password").Input("violet harbour lantern 7");
        cut.Find("form").Submit();

        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Equal(nav.BaseUri, nav.Uri));
        Assert.Equal("session-token", tokens.Token);
    }

    [Fact]
    public void TheForgotLink_IsShownWithThePasswordForm_AndOpensTheHint()
    {
        var (cut, _, _) = Render(offered: true);
        cut.WaitForAssertion(() => cut.Find("form"));
        var link = cut.FindAll("form button").First(b => b.TextContent.Trim() == "ForgotLink");
        Assert.Equal("false", link.GetAttribute("aria-expanded"));
        Assert.DoesNotContain("ForgotIntro", cut.Markup);

        link.Click();

        Assert.Equal("true", cut.FindAll("form button").First(b => b.TextContent.Trim() == "ForgotLink").GetAttribute("aria-expanded"));
        Assert.Equal("ForgotIntro", cut.Find("#password-forgot-hint").TextContent.Trim());
    }

    [Fact]
    public void TheForgotLink_IsNotShownWhenPasswordSignInIsSwitchedOff()
    {
        var (cut, _, _) = Render(offered: false);
        Assert.DoesNotContain("ForgotLink", cut.Markup);
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
        Services.AddSingleton<IFileDownloadService>(new RecordingFileDownloads());
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

/// <summary>Login.razor: after "Forgot your password?" the next passkey or recovery-code sign-in
/// lands on the password card (the fresh sign-in is the proof); without it nothing changes.</summary>
public class LoginForgotPasswordTests : ConsentBunitTest
{
    private sealed class FakeNativeSignIn : INativeAppAuth
    {
        public bool IsAvailable => true;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>("browser-token");
    }

    private (IRenderedComponent<StudyLife.Client.Pages.Login> Cut, SessionTokenStore Tokens, NavigationManager Nav) Render(INativeAppAuth? native = null)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = true });
        server.OnJson("GET api/auth/demo", new DemoInfoDto { Demo = false });
        server.OnJson("POST api/auth/recovery/login", new PasskeyCompleteResponseDto { Token = "recovery-token" });
        var tokens = new SessionTokenStore(new StubJSRuntime());
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(tokens);
        Services.AddSingleton(native ?? new NoNativeAppAuth());
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<StudyLife.Client.Pages.Login>();
        cut.WaitForAssertion(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "ForgotLink"));
        return (cut, tokens, ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>());
    }

    private static void OpenForgot(IRenderedComponent<StudyLife.Client.Pages.Login> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "ForgotLink").Click();

    private static void RecoverySignIn(IRenderedComponent<StudyLife.Client.Pages.Login> cut)
    {
        cut.FindAll("button").First(b => b.TextContent.Trim() == "RecoveryLoginLink").Click();
        cut.Find("input[placeholder='XXXX-XXXX-XXXX']").Input("ABCD-EFGH-JKLM");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "RecoveryLoginButton").Click();
    }

    [Fact]
    public void RecoverySignIn_FromTheForgotPath_LandsOnThePasswordCard()
    {
        var (cut, tokens, nav) = Render();
        OpenForgot(cut);
        RecoverySignIn(cut);

        cut.WaitForAssertion(() => Assert.EndsWith("/setup?forgotPassword=true#password", nav.Uri));
        Assert.Equal("recovery-token", tokens.Token);
    }

    [Fact]
    public void RecoverySignIn_WithoutTheForgotPath_StaysOnSetup()
    {
        var (cut, _, nav) = Render();
        RecoverySignIn(cut);

        cut.WaitForAssertion(() => Assert.EndsWith("/setup", nav.Uri));
    }

    [Fact]
    public void PasskeySignIn_InTheApp_FromTheForgotPath_LandsOnThePasswordCard()
    {
        var (cut, tokens, nav) = Render(new FakeNativeSignIn());
        OpenForgot(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "LoginButton").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/setup?forgotPassword=true#password", nav.Uri));
        Assert.Equal("browser-token", tokens.Token);
    }

    [Fact]
    public void PasskeySignIn_InTheApp_WithoutTheForgotPath_GoesToTheDashboard()
    {
        var (cut, _, nav) = Render(new FakeNativeSignIn());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "LoginButton").Click();

        cut.WaitForAssertion(() => Assert.Equal(nav.BaseUri, nav.Uri));
    }
}
