// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The pure presence check behind the registration form.</summary>
public class RegistrationValidatorTests
{
    [Fact]
    public void Passkey_OnlyNeedsTheName()
    {
        Assert.Equal([RegistrationField.DisplayName], RegistrationValidator.MissingFields("  ", "", "", usePassword: false));
        Assert.Empty(RegistrationValidator.MissingFields("Alex", "", "", usePassword: false));
    }

    [Fact]
    public void Password_ListsEveryGapInFormOrder()
    {
        Assert.Equal(
            [RegistrationField.DisplayName, RegistrationField.Username, RegistrationField.Password],
            RegistrationValidator.MissingFields("", " ", "", usePassword: true));
        Assert.Equal([RegistrationField.Password], RegistrationValidator.MissingFields("Alex", "alex", null, usePassword: true));
        Assert.Empty(RegistrationValidator.MissingFields("Alex", "alex", " pw ", usePassword: true));
    }
}

/// <summary>Register.razor: the buttons stay enabled and name what is missing, and the setup code
/// only shows up without an invite. Texts render as their keys (see AiConsentFixtures.Register).</summary>
public class RegisterPageTests : ConsentBunitTest
{
    private (IRenderedComponent<Register> Cut, ScriptedServer Server) Render(bool passwordOffered, string? invite = null)
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = passwordOffered });
        var js = new StubJSRuntime();
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(js));
        Services.AddSingleton<INativeAppAuth>(new NoNativeAppAuth());
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = ((IServiceProvider)Ctx.Services).GetRequiredService<NavigationManager>();
        if (invite is not null) nav.NavigateTo("/register?invite=" + invite);
        var cut = Render<Register>();
        cut.WaitForState(() => server.Count("GET api/auth/methods") > 0);
        cut.WaitForAssertion(() => cut.Find("#register-name"));
        return (cut, server);
    }

    private sealed class FakeSignal : IConnectivitySignal
    {
        public event Action? Restored;
        public void Raise() => Restored?.Invoke();
    }

    [Fact]
    public void TheMethodChoice_AppearsOnceTheServerCanBeAsked_AfterConnectivityReturns()
    {
        var server = new ScriptedServer();
        server.On("GET api/auth/methods", _ => throw new HttpRequestException("offline"));
        var signal = new FakeSignal();
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(new StubJSRuntime()));
        Services.AddSingleton<INativeAppAuth>(new NoNativeAppAuth());
        Services.AddSingleton<IConnectivitySignal>(signal);
        Services.AddI18nText();
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<Register>();
        cut.WaitForAssertion(() => cut.Find("#register-name"));
        Assert.Empty(cut.FindAll("input[name=register-method]"));

        server.OnJson("GET api/auth/methods", new AuthMethodsDto { PasswordSignIn = true });
        signal.Raise();

        cut.WaitForAssertion(() => cut.Find("input[name=register-method]"));
    }

    [Fact]
    public void Passkey_ButtonIsEnabledWithEmptyFields_AndClickNamesTheNameField()
    {
        var (cut, server) = Render(passwordOffered: false);
        var button = cut.Find("button.btn-primary");
        Assert.False(button.HasAttribute("disabled"));

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("DisplayNameLabel", cut.Find("[role=alert]").TextContent);
            Assert.Equal("true", cut.Find("#register-name").GetAttribute("aria-invalid"));
        });
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("POST api/auth/register"));
    }

    [Fact]
    public void Password_ButtonIsEnabledWithEmptyFields_AndSubmitNamesAllMissingFieldsWithoutCallingTheApi()
    {
        var (cut, server) = Render(passwordOffered: true);
        cut.WaitForAssertion(() => cut.Find("input[name=register-method]"));
        cut.FindAll("input[name=register-method]")[1].Change(true);
        cut.WaitForAssertion(() => cut.Find("#register-username"));
        Assert.False(cut.Find("form button[type=submit]").HasAttribute("disabled"));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("DisplayNameLabel", cut.Find("[role=alert]").TextContent));
        var message = cut.Find("[role=alert]").TextContent;
        Assert.Contains("DisplayNameLabel", message);
        Assert.Contains("UsernameLabel", message);
        Assert.Contains("PasswordLabel", message);
        Assert.Equal("true", cut.Find("#register-name").GetAttribute("aria-invalid"));
        Assert.Equal("true", cut.Find("#register-username").GetAttribute("aria-invalid"));
        Assert.Equal("true", cut.Find("#register-password").GetAttribute("aria-invalid"));
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("POST api/auth/"));
    }

    [Fact]
    public void Password_OnlyTheEmptyFieldIsReported()
    {
        var (cut, _) = Render(passwordOffered: true);
        cut.WaitForAssertion(() => cut.Find("input[name=register-method]"));
        cut.FindAll("input[name=register-method]")[1].Change(true);
        cut.WaitForAssertion(() => cut.Find("#register-username"));
        cut.Find("#register-name").Input("Alex");
        cut.Find("#register-username").Input("alex");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("PasswordLabel", cut.Find("[role=alert]").TextContent));
        var message = cut.Find("[role=alert]").TextContent;
        Assert.Contains("PasswordLabel", message);
        Assert.DoesNotContain("UsernameLabel", message);
        Assert.Null(cut.Find("#register-name").GetAttribute("aria-invalid"));
    }

    [Fact]
    public void WithoutAnInvite_TheSetupCodeFieldIsShown()
    {
        var (cut, _) = Render(passwordOffered: false);
        Assert.Single(cut.FindAll("#register-setup-secret"));
    }

    [Fact]
    public void WithAnInvite_TheSetupCodeFieldIsHidden_ForBothMethods()
    {
        var (cut, _) = Render(passwordOffered: true, invite: "abc123");
        Assert.Empty(cut.FindAll("#register-setup-secret"));

        cut.WaitForAssertion(() => cut.Find("input[name=register-method]"));
        cut.FindAll("input[name=register-method]")[1].Change(true);
        cut.WaitForAssertion(() => cut.Find("#register-username"));
        Assert.Empty(cut.FindAll("#register-setup-secret"));
    }
}
