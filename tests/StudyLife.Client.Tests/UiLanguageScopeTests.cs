// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>
/// The runtime path behind the store screenshot of the English UI showing "1,66" and "19.05.26":
/// LocalDate.Language is a static defaulting to German and the layout learns the real language only
/// after its first awaits, while the page body was already rendered (and, with unchanged parameters,
/// never re-rendered). The scope must hold the body back until the language is known.
/// </summary>
[Collection(LocalDateStateCollection.Name)]
public class UiLanguageScopeTests : IDisposable
{
    private readonly string _language = LocalDate.Language;

    public void Dispose() => LocalDate.Language = _language;

    private static RenderFragment SummaryCard() => b =>
    {
        b.OpenComponent<StatsSummaryCard>(0);
        b.AddAttribute(1, nameof(StatsSummaryCard.AverageGradeLabel), "1,66");
        b.AddAttribute(2, nameof(StatsSummaryCard.AvgGradeLabel), "Avg. grade");
        b.CloseComponent();
    };

    [Fact]
    public void The_body_is_not_rendered_while_the_language_is_unknown()
    {
        LocalDate.Language = "de"; // the static default a fresh start sees
        using var ctx = new BunitContext();

        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, null).Add(c => c.ChildContent, SummaryCard()));

        Assert.DoesNotContain("1,66", cut.Markup);
        Assert.DoesNotContain("1.66", cut.Markup);
    }

    [Fact]
    public void The_body_renders_in_english_once_the_language_arrives_after_the_first_render()
    {
        LocalDate.Language = "de";
        using var ctx = new BunitContext();
        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, null).Add(c => c.ChildContent, SummaryCard()));

        cut.Render(p => p.Add(c => c.Language, "en").Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1.66", cut.Markup);
        Assert.DoesNotContain("1,66", cut.Markup);
    }

    [Fact]
    public void Cards_that_were_already_rendered_follow_a_live_language_switch()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, "de").Add(c => c.ChildContent, SummaryCard()));
        Assert.Contains("1,66", cut.Markup);

        cut.Render(p => p.Add(c => c.Language, "en").Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1.66", cut.Markup);
        Assert.DoesNotContain("1,66", cut.Markup);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("xx")]
    public void A_language_that_is_empty_or_unsupported_still_renders_the_body_in_the_fallback(string language)
    {
        LocalDate.Language = "de";
        using var ctx = new BunitContext();

        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, language).Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1.66", cut.Markup);
        Assert.Equal(UiLanguage.Fallback, LocalDate.Language);
    }

    [Fact]
    public void A_region_tag_renders_the_body_in_its_language()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, "de-DE").Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1,66", cut.Markup);
        Assert.Equal("de", LocalDate.Language);
    }

    /// <summary>
    /// The v3.39.11 regression (blank page body in the English native app, German fine): the native
    /// shell decides English and calls the library's SetCurrentLanguageAsync("en"), which returns early
    /// because the library's built-in default already is "en"; its own initial detection then reports an
    /// empty code, so MainLayout handed "" to the scope and the scope rendered nothing. This runs that
    /// sequence against the real I18nText service and MainLayout's way of reading the language.
    /// </summary>
    [Theory]
    [InlineData("en", "1.66")]
    [InlineData("de", "1,66")]
    public async Task The_native_app_start_renders_the_body_in_the_system_language(string system, string expected)
    {
        LocalDate.Language = "de";
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // What the native web view's own detection yields for the library: nothing usable.
        ctx.Services.AddI18nText(o => o.GetInitialLanguageAsync = (_, _) => ValueTask.FromResult(""));
        var i18n = ctx.Services.GetRequiredService<Toolbelt.Blazor.I18nText.I18nText>();
        var webView = new NativeWebView();

        // AppRoot.ApplyLanguageAsync: the system language is prescribed.
        await UiLanguageStartup.ApplyAsync(webView, i18n.SetCurrentLanguageAsync, hasPriorUse: false, prescribed: system);
        // MainLayout.OnInitializedAsync: the language handed to the scope.
        var language = await UiLanguageStartup.GetCurrentCodeAsync(webView, i18n.GetCurrentLanguageAsync);
        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, language).Add(c => c.ChildContent, SummaryCard()));

        Assert.Equal(system, language);
        Assert.Contains(expected, cut.Markup);
    }

    /// <summary>A native web view: localStorage keeps what the start-up writes, navigator.languages is the system's.</summary>
    private sealed class NativeWebView : IJSRuntime
    {
        private string? _stored;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            object? answer = null;
            if (identifier == "localStorage.setItem" && args is [UiLanguageStartup.StorageKey, string value]) _stored = value;
            else if (identifier == "localStorage.getItem") answer = _stored;
            else if (identifier == "navigator.languages.join") answer = "en-US";
            return ValueTask.FromResult((TValue)answer!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
