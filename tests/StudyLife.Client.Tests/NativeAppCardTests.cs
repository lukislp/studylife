// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The native-only Setup pieces: the server switch with its confirmation and the Health
/// connection. Texts render as their keys here (see AiConsentFixtures.Register).</summary>
public class NativeAppCardTests : ConsentBunitTest
{
    private sealed class FakeNativeAuth : INativeAppAuth
    {
        public string? Host { get; set; }
        public int ChangeCalls { get; private set; }
        public bool IsAvailable => Host is not null;
        public Task<string?> AuthenticateAsync(string startPage) => Task.FromResult<string?>(null);
        public string? ServerHost => Host;
        public Task ChangeServerAsync()
        {
            ChangeCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealth : INativeHealthData
    {
        public NativeHealthAccess Access { get; set; }
        public int RequestCalls { get; private set; }
        public bool IsAvailable => Access != NativeHealthAccess.Unavailable;
        public Task<NativeHealthAccess> GetAccessAsync() => Task.FromResult(Access);
        public Task<NativeHealthAccess> RequestAccessAsync()
        {
            RequestCalls++;
            Access = NativeHealthAccess.Granted;
            return Task.FromResult(Access);
        }
    }

    private (FakeNativeAuth Auth, FakeHealth Health) Arrange(string? host, NativeHealthAccess access)
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = new FakeNativeAuth { Host = host };
        var health = new FakeHealth { Access = access };
        Services.AddSingleton<INativeAppAuth>(auth);
        Services.AddSingleton<INativeHealthData>(health);
        Services.AddI18nText();
        return (auth, health);
    }

    [Fact]
    public void InTheBrowser_NothingIsRendered()
    {
        Arrange(null, NativeHealthAccess.Unavailable);

        var cut = Render<SetupNativeAppCard>();

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void ChangeServer_AsksBeforeItDoesAnything()
    {
        var (auth, _) = Arrange("studylife.example.com", NativeHealthAccess.Unavailable);
        var cut = Render<NativeServerSwitch>();

        Assert.Empty(cut.FindAll("[role=dialog]"));
        cut.WaitForElement("button").Click();

        Assert.Single(cut.FindAll("[role=dialog]"));
        Assert.Equal(0, auth.ChangeCalls);
    }

    [Fact]
    public void ChangeServer_CancelKeepsTheServer()
    {
        var (auth, _) = Arrange("studylife.example.com", NativeHealthAccess.Unavailable);
        var cut = Render<NativeServerSwitch>();
        cut.WaitForElement("button").Click();

        cut.Find("[role=dialog] .btn-secondary").Click();

        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Equal(0, auth.ChangeCalls);
    }

    [Fact]
    public void ChangeServer_ConfirmRunsTheHandOverOnce()
    {
        var (auth, _) = Arrange("studylife.example.com", NativeHealthAccess.Unavailable);
        var cut = Render<NativeServerSwitch>();
        cut.WaitForElement("button").Click();

        cut.Find("[role=dialog] .btn-danger").Click();

        Assert.Equal(1, auth.ChangeCalls);
    }

    [Fact]
    public void Health_IsNotRequestedByShowingTheCard()
    {
        var (_, health) = Arrange("studylife.example.com", NativeHealthAccess.NotAsked);

        var cut = Render<SetupNativeAppCard>();

        cut.WaitForElement("#native-health button");
        Assert.Equal(0, health.RequestCalls);
    }

    [Fact]
    public void Health_TheButtonIsTheOnlyThingThatAsks()
    {
        var (_, health) = Arrange(null, NativeHealthAccess.NotAsked);
        var cut = Render<SetupNativeAppCard>();

        cut.WaitForElement("#native-health button").Click();

        Assert.Equal(1, health.RequestCalls);
        Assert.Empty(cut.FindAll("#native-health button"));
    }

    [Theory]
    [InlineData(NativeHealthAccess.Granted)]
    [InlineData(NativeHealthAccess.Declined)]
    public void Health_ADecidedStateNeverOffersTheRequestAgain(NativeHealthAccess access)
    {
        var (_, health) = Arrange(null, access);

        var cut = Render<SetupNativeAppCard>();

        cut.WaitForElement("#native-health [role=status]");
        Assert.Empty(cut.FindAll("#native-health button"));
        Assert.Single(cut.FindAll("#native-health [role=status]"));
        Assert.Equal(0, health.RequestCalls);
    }
}
