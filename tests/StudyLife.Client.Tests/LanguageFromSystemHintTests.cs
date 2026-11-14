// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components;
using StudyLife.Client.Services;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>Native shells show where the language is changed instead of a picker.</summary>
public class LanguageFromSystemHintTests : ConsentBunitTest
{
    private sealed class FakePlatform(bool fromSystem) : IClientPlatform
    {
        public int Opened { get; private set; }
        public string Name => fromSystem ? "ios" : "web";
        public bool LanguageFromSystem => fromSystem;
        public Task OpenLanguageSettingsAsync()
        {
            Opened++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void The_button_opens_the_system_language_setting()
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var platform = new FakePlatform(fromSystem: true);
        Services.AddSingleton<IClientPlatform>(platform);
        Services.AddI18nText();

        var cut = Render<LanguageFromSystemHint>();
        cut.WaitForElement("button").Click();

        Assert.Equal(1, platform.Opened);
    }

    [Fact]
    public void The_browser_keeps_its_picker()
    {
        IClientPlatform browser = new BrowserClientPlatform();
        Assert.False(browser.LanguageFromSystem);
    }
}
