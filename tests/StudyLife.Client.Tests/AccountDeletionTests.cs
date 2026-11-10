// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>The client half of account deletion: the API calls, how failures are told apart and that
/// only a successful self-deletion clears the local session.</summary>
public class AccountDeletionStateTests
{
    private static (AccountDeletionState State, SessionTokenStore Tokens, ScriptedServer Server) Create()
    {
        var server = new ScriptedServer();
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        return (new AccountDeletionState(http, tokens), tokens, server);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string type) =>
        new(status) { Content = JsonContent.Create(new { type, title = type, status = (int)status }) };

    [Fact]
    public async Task Preview_ReturnsTheServersAnswer_AndNullWhenItCannotBeReached()
    {
        var (state, _, server) = Create();
        Assert.Null(await state.GetPreviewAsync()); // 404 from the scripted server

        server.OnJson("GET api/auth/account/deletion-preview", new AccountDeletionPreviewDto { Available = true, OwnedClientCount = 2 });
        var preview = await state.GetPreviewAsync();
        Assert.True(preview!.Available);
        Assert.Equal(2, preview.OwnedClientCount);
    }

    [Fact]
    public async Task DeleteOwnAccount_SendsTheProof_AndClearsTheLocalSessionOnSuccess()
    {
        var (state, tokens, server) = Create();
        await tokens.SetTokenAsync("session-token");
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto { InstallationReset = true });

        var attempt = await state.DeleteOwnAccountAsync(new AccountDeletionProof(null, null, " ABCD-EFGH-JKLM "));

        Assert.True(attempt.Succeeded);
        Assert.True(attempt.Result!.InstallationReset);
        Assert.Null(tokens.Token); // the same clean-up a logout runs
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("ABCD-EFGH-JKLM", body["recoveryCode"]!.GetValue<string>());
        Assert.False(body.ContainsKey("optionsId"));
    }

    [Fact]
    public async Task DeleteOwnAccount_CarriesAPasskeyAssertionAsTheNestedResponse()
    {
        var (state, _, server) = Create();
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto());

        await state.DeleteOwnAccountAsync(new AccountDeletionProof("opt-1", "{\"id\":\"cred\",\"type\":\"public-key\"}", null));

        var body = JsonNode.Parse(Assert.Single(server.Bodies))!.AsObject();
        Assert.Equal("opt-1", body["optionsId"]!.GetValue<string>());
        Assert.Equal("cred", body["response"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "reauthentication-failed", AccountDeletionFailure.ProofRejected)]
    [InlineData(HttpStatusCode.Conflict, "owner-has-other-users", AccountDeletionFailure.OwnerHasOtherUsers)]
    [InlineData(HttpStatusCode.Forbidden, "owner-only", AccountDeletionFailure.OwnerOnly)]
    [InlineData(HttpStatusCode.Forbidden, "demo-readonly", AccountDeletionFailure.Demo)]
    public async Task Failures_AreToldApartByTheirProblemType_AndNeverLogTheUserOut(
        HttpStatusCode status, string type, AccountDeletionFailure expected)
    {
        var (state, tokens, server) = Create();
        await tokens.SetTokenAsync("session-token");
        server.On("DELETE api/auth/account", _ => Problem(status, type));

        var attempt = await state.DeleteOwnAccountAsync(new AccountDeletionProof(null, null, "x"));

        Assert.Equal(expected, attempt.Failure);
        Assert.Equal("session-token", tokens.Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AccountDeletionFailure.ProofMissing)]
    [InlineData(HttpStatusCode.NotFound, AccountDeletionFailure.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, AccountDeletionFailure.TooManyAttempts)]
    [InlineData(HttpStatusCode.Forbidden, AccountDeletionFailure.Demo)] // the demo write block's plain 403
    [InlineData(HttpStatusCode.InternalServerError, AccountDeletionFailure.Other)]
    public async Task PlainStatusCodes_AreClassified(HttpStatusCode status, AccountDeletionFailure expected)
    {
        var (state, _, server) = Create();
        server.On("DELETE api/auth/account", _ => new HttpResponseMessage(status));

        Assert.Equal(expected, (await state.DeleteOwnAccountAsync(new AccountDeletionProof(null, null, null))).Failure);
    }

    [Fact]
    public async Task AnUnreachableServer_IsNotAFailureOfTheProof()
    {
        var (state, tokens, server) = Create();
        await tokens.SetTokenAsync("session-token");
        server.On("DELETE api/auth/account", _ => throw new HttpRequestException("offline"));

        var attempt = await state.DeleteOwnAccountAsync(new AccountDeletionProof(null, null, "x"));

        Assert.Equal(AccountDeletionFailure.Network, attempt.Failure);
        Assert.Equal("session-token", tokens.Token);
    }

    [Fact]
    public async Task RemovingAnotherUser_KeepsTheOwnersSession()
    {
        var (state, tokens, server) = Create();
        await tokens.SetTokenAsync("owner-session");
        server.OnJson("DELETE api/auth/users/7", new AccountDeletionResultDto { AddonCleanupPending = 1 });

        var attempt = await state.RemoveUserAsync(7, new AccountDeletionProof(null, null, "x"));

        Assert.True(attempt.Succeeded);
        Assert.Equal(1, attempt.Result!.AddonCleanupPending);
        Assert.Equal("owner-session", tokens.Token);
    }

    [Fact]
    public async Task UserListAndPasskeyChallenge_AreNullWhenRefused()
    {
        var (state, _, server) = Create();
        server.On("GET api/auth/users", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        server.On("POST api/auth/account/reauth/begin", _ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        Assert.Null(await state.ListUsersAsync());
        Assert.Null(await state.BeginPasskeyProofAsync());

        server.OnJson("GET api/auth/users", new List<UserListItemDto> { new() { Id = 3, DisplayName = "Guest" } });
        Assert.Equal("Guest", Assert.Single((await state.ListUsersAsync())!).DisplayName);
    }
}

/// <summary>The danger card and its dialog. Texts are the key names themselves (see the helper below),
/// so every assertion also pins which key the markup uses.</summary>
public class SetupDeleteAccountCardTests : ConsentBunitTest
{
    private sealed class FakeNativeAuth : INativeAppAuth
    {
        public bool IsAvailable { get; init; }
        public bool SupportsInAppPasskeys { get; init; }
        public int AccountDeletedCalls;
        public string? SignInToken;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult(SignInToken);
        public Task AccountDeletedAsync() { AccountDeletedCalls++; return Task.CompletedTask; }
    }

    private static StudyLife.Client.I18nText.AccountDeletionText KeyTexts()
    {
        var texts = new StudyLife.Client.I18nText.AccountDeletionText();
        // The generated table type exposes its entries as public fields.
        foreach (var field in typeof(StudyLife.Client.I18nText.AccountDeletionText).GetFields()
                     .Where(f => f.FieldType == typeof(string) && !f.IsStatic))
            field.SetValue(texts, field.Name);
        // The format strings keep their placeholders so the substituted names are visible.
        texts.RemoveUserAria = "RemoveUserAria:{0}";
        texts.RemoveUserTitle = "RemoveUserTitle:{0}";
        texts.RemoveUserWarning = "RemoveUserWarning:{0}";
        texts.UserRemoved = "UserRemoved:{0}";
        texts.OwnedAppsText = "OwnedAppsText:{0}";
        return texts;
    }

    private (IRenderedComponent<SetupDeleteAccountCard> Cut, ScriptedServer Server, FakeNativeAuth Native, SessionTokenStore Tokens)
        Render(AccountDeletionPreviewDto preview, List<UserListItemDto>? users = null, FakeNativeAuth? native = null)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/account/deletion-preview", preview);
        server.OnJson("GET api/auth/users", users ?? []);
        var js = new StubJSRuntime();
        var tokens = new SessionTokenStore(js);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        native ??= new FakeNativeAuth();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(http);
        Services.AddSingleton(tokens);
        Services.AddSingleton(new AppStateService(http, js, NullLogger<AppStateService>.Instance, tokens));
        Services.AddSingleton<INativeAppAuth>(native);
        Services.AddSingleton<INativeFileExport>(new NoNativeFileExport());
        var cut = Render<SetupDeleteAccountCard>(p => p.Add(c => c.Text, KeyTexts()));
        cut.WaitForState(() => !cut.Markup.Contains("Working"));
        return (cut, server, native, tokens);
    }

    private static AccountDeletionPreviewDto Available(int owned = 0, bool last = false, bool owner = false) => new()
    {
        Available = true,
        OwnedClientCount = owned,
        InstallationWillReset = last,
        IsOwner = owner,
    };

    [Fact]
    public void AnAvailableAccount_ShowsTheDangerButton_AndNothingElse()
    {
        var (cut, _, _, _) = Render(Available());

        Assert.Equal("OpenButton", cut.Find("button.btn-danger").TextContent.Trim());
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.DoesNotContain("OwnerBlocked", cut.Markup);
    }

    [Fact]
    public void TheDemoInstance_SaysItIsNotAvailable_WithoutAButton()
    {
        var (cut, _, _, _) = Render(new AccountDeletionPreviewDto { Available = false, UnavailableReason = "demo" });

        Assert.Contains("DemoUnavailable", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void TheOwnerWithOtherUsers_GetsTheReason_AndTheListToRemoveThemFrom()
    {
        var (cut, _, _, _) = Render(
            new AccountDeletionPreviewDto { Available = false, UnavailableReason = "owner-has-other-users", IsOwner = true, OtherUserCount = 2 },
            [new UserListItemDto { Id = 4, DisplayName = "Guest One" }, new UserListItemDto { Id = 5, DisplayName = "Guest Two" }]);

        Assert.Contains("OwnerBlocked", cut.Markup);
        Assert.DoesNotContain("OpenButton", cut.Markup);
        var rows = cut.FindAll("[role=listitem]");
        Assert.Equal(2, rows.Count);
        // Every remove button names its user for assistive technology.
        Assert.Equal("RemoveUserAria:Guest One", rows[0].QuerySelector("button")!.GetAttribute("aria-label"));
    }

    [Fact]
    public void TheDialog_IsAccessible_AndNeedsTheExplicitAcknowledgementBeforeContinuing()
    {
        var (cut, _, _, _) = Render(Available(owned: 2, last: true));

        cut.Find("button.btn-danger").Click();

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("DialogTitle", cut.Find("#" + dialog.GetAttribute("aria-labelledby")).TextContent);
        Assert.Contains("aria-label=\"CloseDialog\"", cut.Markup);
        // What goes, what stays, the apps the user owns and the first-run reset are all stated.
        Assert.Contains("ItemData", cut.Markup);
        Assert.Contains("NotDeletedBackups", cut.Markup);
        Assert.Contains("NotDeletedPasskeys", cut.Markup);
        Assert.Contains("NotDeletedCalendar", cut.Markup);
        Assert.Contains("OwnedAppsText:2", cut.Markup);
        Assert.Contains("LastUserText", cut.Markup);
        Assert.Contains("ExportButton", cut.Markup);

        var next = cut.Find(".modal-footer button.btn-danger");
        Assert.True(next.HasAttribute("disabled"));
        cut.Find("input[type=checkbox]").Change(true);
        Assert.False(cut.Find(".modal-footer button.btn-danger").HasAttribute("disabled"));
    }

    [Fact]
    public void Escape_ClosesTheDialog_WithoutDeletingAnything()
    {
        var (cut, server, _, _) = Render(Available());
        cut.Find("button.btn-danger").Click();

        cut.Find("[role=dialog]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Equal(0, server.Count("DELETE api/auth/account"));
    }

    private static void ToReauth(IRenderedComponent<SetupDeleteAccountCard> cut)
    {
        cut.Find("button.btn-danger").Click();
        cut.Find("input[type=checkbox]").Change(true);
        cut.Find(".modal-footer button.btn-danger").Click();
    }

    [Fact]
    public void AfterContinuing_NothingIsDeletedYet_TheReauthenticationStepAsksForTheProof()
    {
        var (cut, server, _, _) = Render(Available());

        ToReauth(cut);

        Assert.Equal("ReauthTitle", cut.Find("#delete-account-title").TextContent);
        Assert.Contains("PasskeyButton", cut.Markup);
        Assert.Contains("RecoveryToggle", cut.Markup);
        Assert.Equal(0, server.Count("DELETE api/auth/account"));
    }

    [Fact]
    public async Task ARecoveryCode_DeletesTheAccount_WipesTheNativeShell_AndLeavesForTheLogin()
    {
        var native = new FakeNativeAuth();
        var (cut, server, _, tokens) = Render(Available(), native: native);
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto());
        await tokens.SetTokenAsync("session-token");

        ToReauth(cut);
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#delete-account-recovery").Input("ABCD-EFGH-JKLM");
        cut.FindAll(".modal-footer button").Last().Click();
        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("DELETE api/auth/account")));

        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/login", nav.Uri));
        Assert.Null(tokens.Token);
        Assert.Equal(1, native.AccountDeletedCalls);
        Assert.Contains("ABCD-EFGH-JKLM", Assert.Single(server.Bodies));
    }

    [Fact]
    public void TheLastAccount_LeavesForTheSetUpScreen()
    {
        var (cut, server, _, _) = Render(Available(last: true));
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto { InstallationReset = true });

        ToReauth(cut);
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#delete-account-recovery").Input("ABCD-EFGH-JKLM");
        cut.FindAll(".modal-footer button").Last().Click();

        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/register", nav.Uri));
    }

    [Fact]
    public async Task ARejectedProof_ShowsAnAnnouncedError_AndKeepsTheSession()
    {
        var (cut, server, native, tokens) = Render(Available());
        server.On("DELETE api/auth/account", _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { type = "reauthentication-failed" }),
        });
        await tokens.SetTokenAsync("session-token");

        ToReauth(cut);
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#delete-account-recovery").Input("WRONG-CODE");
        cut.FindAll(".modal-footer button").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal("ErrorProofRejected", cut.Find("[role=alert]").TextContent.Trim()));
        Assert.Equal("session-token", tokens.Token);
        Assert.Equal(0, native.AccountDeletedCalls);
        Assert.NotEmpty(cut.FindAll("[role=dialog]")); // still open, the user can retry or cancel
    }

    [Fact]
    public void ANativeShellWithoutInAppPasskeys_SignsInAgainThroughTheBrowserAsItsProof()
    {
        var native = new FakeNativeAuth { IsAvailable = true, SupportsInAppPasskeys = false, SignInToken = "fresh-token" };
        var (cut, server, _, tokens) = Render(Available(), native: native);
        server.OnJson("DELETE api/auth/account", new AccountDeletionResultDto());

        ToReauth(cut);
        Assert.Contains("BrowserSignInText", cut.Markup);
        cut.FindAll(".modal-footer button").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("DELETE api/auth/account")));
        // No assertion and no code in the body: the freshly signed-in session is the proof.
        Assert.Equal("{}", Assert.Single(server.Bodies));
        cut.WaitForAssertion(() => Assert.Equal(1, native.AccountDeletedCalls));
        Assert.Null(tokens.Token);
    }

    [Fact]
    public async Task TheOwnerRemovingAGuest_NamesThemInTheConfirmation_AndStaysOnThePage()
    {
        var (cut, server, native, tokens) = Render(
            new AccountDeletionPreviewDto { Available = false, UnavailableReason = "owner-has-other-users", IsOwner = true, OtherUserCount = 1 },
            [new UserListItemDto { Id = 9, DisplayName = "Guest One" }]);
        server.OnJson("DELETE api/auth/users/9", new AccountDeletionResultDto { AddonCleanupPending = 1 });
        await tokens.SetTokenAsync("owner-session");

        cut.Find("[role=listitem] button").Click();
        Assert.Equal("RemoveUserTitle:Guest One", cut.Find("#delete-account-title").TextContent);
        Assert.Contains("RemoveUserWarning:Guest One", cut.Markup);
        Assert.DoesNotContain("ItemData", cut.Markup); // the removal dialog is about that user, not about the owner
        cut.Find("input[type=checkbox]").Change(true);
        cut.Find(".modal-footer button.btn-danger").Click();
        cut.FindAll(".modal-footer button").First(b => b.TextContent.Trim() == "RecoveryToggle").Click();
        cut.Find("#delete-account-recovery").Input("ABCD-EFGH-JKLM");
        cut.FindAll(".modal-footer button").Last().Click();

        cut.WaitForAssertion(() => Assert.Contains("UserRemoved:Guest One AddonPending", cut.Markup));
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Equal("owner-session", tokens.Token);
        Assert.Equal(0, native.AccountDeletedCalls);
    }
}
