// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;
using StudyLife.Shared;
using static StudyLife.Client.Tests.AiConsentFixtures;

namespace StudyLife.Client.Tests;

/// <summary>bUnit context owned per test and disposed asynchronously: the I18nText helper script
/// only implements IAsyncDisposable, which the synchronous disposal of a BunitContext subclass
/// cannot cope with.</summary>
public abstract class ConsentBunitTest : IAsyncLifetime
{
    protected BunitContext Ctx { get; } = new();

    protected IServiceCollection Services => Ctx.Services;

    protected IRenderedComponent<TComponent> Render<TComponent>(
        Action<ComponentParameterCollectionBuilder<TComponent>>? parameters = null)
        where TComponent : Microsoft.AspNetCore.Components.IComponent => Ctx.Render(parameters);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Ctx.DisposeAsync();
}

/// <summary>
/// The consent dialog and its two callers. The dialog only renders what AiConsentState asks it
/// to; the callers (Setup card, chat modal) only ask "ensure consent, then continue" and render.
/// The dialog is rendered with explicit texts; the callers render their i18n keys (see
/// AiConsentFixtures.Register).
/// </summary>
public class AiConsentModalTests : ConsentBunitTest
{
    private static readonly StudyLife.Client.I18nText.AiConsentText Texts = new()
    {
        Heading = "Allow AI?",
        WhatText = "What is shared.",
        WhomHeading = "Who receives this data:",
        WhomUnknown = "The provider the operator configured.",
        LeavesServerText = "It leaves this server.",
        OptionalText = "Optional.",
        WithdrawText = "Withdraw any time.",
        ExistingUserText = "You already use AI.",
        AgreeButton = "Agree",
        DeclineButton = "Not now",
        ProviderChangedNotice = "The provider changed.",
        ErrorGeneric = "Error.",
    };

    private (IRenderedComponent<AiConsentModal> Cut, AiConsentState Consent, ScriptedServer Server) Render(ScriptedServer? server = null)
    {
        server ??= new ScriptedServer();
        var state = NewAppState(server);
        Services.AddSingleton(state);
        var cut = Render<AiConsentModal>(p => p.Add(c => c.Text, Texts));
        return (cut, state.AiConsent, server);
    }

    private static Task<bool> AskAsync(AiConsentState consent, ScriptedServer server, AiConsentStatusDto status)
    {
        server.OnJson("GET api/ai/consent", status);
        return consent.EnsureConsentAsync();
    }

    [Fact]
    public void WithoutAQuestion_NothingIsRendered()
    {
        var (cut, _, _) = Render();

        Assert.Empty(cut.FindAll("[role=dialog]"));
    }

    [Fact]
    public async Task Question_RendersALabelledModalDialog_NamingEveryProviderAndModelOnce()
    {
        var (cut, consent, server) = Render();

        _ = AskAsync(consent, server, Status());
        cut.WaitForElement("[role=dialog]");

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("Allow AI?", cut.Find("#" + dialog.GetAttribute("aria-labelledby")).TextContent);
        Assert.False(string.IsNullOrWhiteSpace(cut.Find("#" + dialog.GetAttribute("aria-describedby")).TextContent));
        var recipients = cut.FindAll("li").Select(li => li.TextContent).ToList();
        // OpenAI/gpt-4o-mini is used for two roles but named once; the local model is named too.
        Assert.Equal(["OpenAI (gpt-4o-mini)", "Ollama (local) (llama3.2)"], recipients);
        Assert.Contains("It leaves this server.", cut.Markup);
        Assert.Contains("Optional.", cut.Markup);
        Assert.Contains("Withdraw any time.", cut.Markup);
        Assert.DoesNotContain(Texts.WhomUnknown, cut.Markup);
        await consent.DeclineAsync();
    }

    [Fact]
    public async Task UnknownProvider_SaysTheProviderTheOperatorConfigured()
    {
        var (cut, consent, server) = Render();

        _ = AskAsync(consent, server, Status(known: false));
        cut.WaitForElement("[role=dialog]");

        Assert.Contains("The provider the operator configured.", cut.Markup);
        Assert.Empty(cut.FindAll("li"));
        await consent.DeclineAsync();
    }

    [Fact]
    public async Task ThereIsNoWayToDismissItWithoutAnAnswer()
    {
        var (cut, consent, server) = Render();
        _ = AskAsync(consent, server, Status());
        cut.WaitForElement("[role=dialog]");

        // The backdrop has no click handler at all (unlike every other modal): a consent needs an
        // explicit answer.
        Assert.Throws<Bunit.MissingEventHandlerException>(() => cut.Find(".modal-overlay").Click());

        Assert.NotEmpty(cut.FindAll("[role=dialog]"));
        Assert.Empty(cut.FindAll(".modal-close")); // no close button
        var buttons = cut.FindAll(".modal-footer button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Not now", "Agree"], buttons);
        await consent.DeclineAsync();
    }

    [Fact]
    public async Task Agree_RecordsTheConsent_AndClosesTheDialog()
    {
        var (cut, consent, server) = Render();
        server.OnJson("POST api/ai/consent/grant", Status(granted: true));
        var ensure = AskAsync(consent, server, Status());
        cut.WaitForElement("[role=dialog]");

        cut.FindAll(".modal-footer button")[1].Click();

        Assert.True(await ensure);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));
        Assert.Equal(1, server.Count("POST api/ai/consent/grant"));
    }

    [Fact]
    public async Task NotNow_ClosesTheDialog_WithoutAnError()
    {
        var (cut, consent, server) = Render();
        var ensure = AskAsync(consent, server, Status());
        cut.WaitForElement("[role=dialog]");

        cut.FindAll(".modal-footer button")[0].Click();

        Assert.False(await ensure);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task ExistingUser_IsToldThatDecliningSwitchesAiOff()
    {
        var (cut, consent, server) = Render();
        server.On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        server.OnJson("GET api/ai/consent", Status(hasKey: true, needsDecision: true));
        var check = consent.CheckAtStartupAsync();
        cut.WaitForElement("[role=dialog]");

        Assert.Contains("You already use AI.", cut.Markup);
        cut.FindAll(".modal-footer button")[0].Click();
        await check;

        Assert.Equal(1, server.Count("POST api/ai/consent/withdraw"));
    }

    [Fact]
    public async Task FailedAgree_ShowsAnAlert_AndKeepsTheDialogOpen()
    {
        var (cut, consent, server) = Render();
        server.On("POST api/ai/consent/grant", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _ = AskAsync(consent, server, Status());
        cut.WaitForElement("[role=dialog]");

        cut.FindAll(".modal-footer button")[1].Click();

        cut.WaitForElement("[role=alert]");
        Assert.NotEmpty(cut.FindAll("[role=dialog]"));
        await consent.DeclineAsync();
    }

    [Fact]
    public async Task ProviderChangedWhileOpen_SaysSo()
    {
        var (cut, consent, server) = Render();
        server.OnJson("GET api/ai/consent", Status(fingerprint: 'a'));
        server.OnJson("POST api/ai/consent/grant", Status(fingerprint: 'b'), HttpStatusCode.Conflict);
        var ensure = consent.EnsureConsentAsync();
        cut.WaitForElement("[role=dialog]");
        server.OnJson("GET api/ai/consent", Status(fingerprint: 'b'));

        cut.FindAll(".modal-footer button")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains("The provider changed.", cut.Markup));
        await consent.DeclineAsync();
        Assert.False(await ensure);
    }
}

/// <summary>The Setup card asks before enabling, says what it must, and can withdraw.</summary>
public class SetupAiIntegrationCardTests : ConsentBunitTest
{
    private (IRenderedComponent<SetupAiIntegrationCard> Cut, AppStateService State, ScriptedServer Server) Render(
        AiConsentStatusDto consent, bool hasKey = false)
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", consent)
            .OnJson("GET api/settings/ai-api-key", new AiApiKeyStatusDto { HasKey = hasKey, CreatedAt = hasKey ? DateTime.UtcNow : null });
        var state = Register(Ctx, server);
        var cut = Render<SetupAiIntegrationCard>();
        cut.WaitForState(() => cut.Markup.Length > 0 || server.Count("GET api/ai/consent") > 0);
        return (cut, state, server);
    }

    [Fact]
    public void WithoutAnAddon_TheCardIsHidden()
    {
        var (cut, _, _) = Render(Status(available: false));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".setup-section")));
    }

    [Fact]
    public void LocalOnlyProvider_SaysOnceThatTheAiRunsOnTheOperatorsServer()
    {
        var (cut, _, _) = Render(Status(required: false));

        cut.WaitForElement(".setup-section");
        Assert.Contains("LocalRunsHere", cut.Markup);
    }

    [Fact]
    public async Task Enable_AsksForConsentFirst_AndStartsNothingWhenTheUserSaysNotNow()
    {
        var (cut, state, server) = Render(Status());
        cut.WaitForElement(".setup-section");

        var click = cut.InvokeAsync(() => cut.Find(".setup-section button.btn-secondary").Click());
        await click;
        var request = await WaitForPendingAsync(state);
        Assert.False(request.ExistingUser);
        await state.AiConsent.DeclineAsync();

        cut.WaitForAssertion(() => Assert.False(cut.Find(".setup-section button.btn-secondary").HasAttribute("disabled")));
        Assert.Equal(0, server.Count("POST api/settings/ai-api-key/generate")); // declining leaves AI off, no request, no error
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Enable_AfterAgreeing_GeneratesTheKey()
    {
        var (cut, state, server) = Render(Status());
        server.OnJson("POST api/ai/consent/grant", Status(granted: true));
        server.OnJson("POST api/settings/ai-api-key/generate", new AiApiKeyGenerateResponseDto { ApiKey = "k", CreatedAt = DateTime.UtcNow });
        cut.WaitForElement(".setup-section");

        _ = cut.InvokeAsync(() => cut.Find(".setup-section button.btn-secondary").Click());
        await WaitForPendingAsync(state);
        await state.AiConsent.AgreeAsync();

        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("POST api/settings/ai-api-key/generate")));
    }

    [Fact]
    public async Task Enable_WithLocalOnlyProvider_NeverAsks()
    {
        var (cut, state, server) = Render(Status(required: false));
        server.OnJson("POST api/settings/ai-api-key/generate", new AiApiKeyGenerateResponseDto { ApiKey = "k", CreatedAt = DateTime.UtcNow });
        cut.WaitForElement(".setup-section");

        await cut.InvokeAsync(() => cut.Find(".setup-section button.btn-secondary").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("POST api/settings/ai-api-key/generate")));
        Assert.Null(state.AiConsent.Pending);
    }

    [Fact]
    public void WhenConsentWasGiven_ItShowsWhen_AndOffersWithdraw()
    {
        var (cut, _, _) = Render(Status(granted: true, hasKey: true), hasKey: true);

        cut.WaitForElement(".setup-section .btn-danger");
        Assert.Contains("ConsentGivenOn", cut.Markup);
    }

    [Fact]
    public void Withdraw_ConfirmsFirst_ThenCallsTheWithdrawEndpoint()
    {
        var (cut, _, server) = Render(Status(granted: true, hasKey: true), hasKey: true);
        server.On("POST api/ai/consent/withdraw", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        cut.WaitForElement(".setup-section .btn-danger");

        // First button of that kind is the existing "disable" action, the consent text sits above it.
        cut.FindAll(".setup-section .btn-danger").Single(b => b.TextContent.Contains("WithdrawButton")).Click();
        Assert.Equal(0, server.Count("POST api/ai/consent/withdraw")); // only a confirmation dialog so far
        cut.Find("[role=dialog] .btn-danger").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, server.Count("POST api/ai/consent/withdraw")));
    }

    private static async Task<AiConsentRequest> WaitForPendingAsync(AppStateService state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (state.AiConsent.Pending is null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the consent dialog never appeared");
            await Task.Delay(10);
        }
        return state.AiConsent.Pending;
    }
}

/// <summary>The chat modal asks before it opens and reacts to the server's 403 problem.</summary>
public class AgentChatModalConsentTests : ConsentBunitTest
{
    private (IRenderedComponent<AgentChatModal> Cut, AppStateService State, ScriptedServer Server, List<bool> Closed) Render(
        AiConsentStatusDto consent, bool hasKey = true)
    {
        var server = new ScriptedServer()
            .OnJson("GET api/ai/consent", consent)
            .OnJson("GET api/settings/ai-api-key", new AiApiKeyStatusDto { HasKey = hasKey, CreatedAt = DateTime.UtcNow });
        var state = Register(Ctx, server);
        var closed = new List<bool>();
        var cut = Render<AgentChatModal>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.OnClose, () => closed.Add(true)));
        return (cut, state, server, closed);
    }

    [Fact]
    public async Task Opening_WithoutConsent_AsksFirst_AndNotNowClosesTheChat()
    {
        var (cut, state, server, closed) = Render(Status());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (state.AiConsent.Pending is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.NotNull(state.AiConsent.Pending);

        await state.AiConsent.DeclineAsync();

        cut.WaitForAssertion(() => Assert.Single(closed));
        Assert.Equal(0, server.Count("GET api/settings/ai-api-key")); // never got as far as the key
    }

    [Fact]
    public void Opening_WithCoveringConsent_GoesStraightToTheChat()
    {
        var (cut, state, _, closed) = Render(Status(granted: true, hasKey: true));

        cut.WaitForElement(".acm-tabs");
        Assert.Null(state.AiConsent.Pending);
        Assert.Empty(closed);
    }

    [Fact]
    public void Opening_WithLocalOnlyProvider_NeverAsks()
    {
        var (cut, state, _, _) = Render(Status(required: false));

        cut.WaitForElement(".acm-tabs");
        Assert.Null(state.AiConsent.Pending);
    }

    [Fact]
    public void ServerSays403ConsentRequired_WhileChatting_TheUserIsAsked()
    {
        var (cut, state, server, _) = Render(Status(granted: true, hasKey: true));
        server.On("POST api/ai/chat", _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(ProblemJson()),
        });
        cut.WaitForElement(".acm-tabs");
        // The consent was withdrawn on another device: the next status call says so.
        server.OnJson("GET api/ai/consent", Status(granted: false, hasKey: true));

        cut.Find(".acm-input-row input").Input("hello");
        cut.Find(".acm-input-row button").Click();

        cut.WaitForAssertion(() => Assert.NotNull(state.AiConsent.Pending));
        Assert.Equal(1, server.Count("POST api/ai/chat"));
        _ = state.AiConsent.DeclineAsync();
    }
}
