// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.JSInterop;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class UiLanguageStartupTests
{
    /// <summary>A web view with a fixed localStorage entry and navigator.languages.</summary>
    private sealed class FakeWebView(string? stored, string? languages, bool languagesThrow = false) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            object? answer = identifier switch
            {
                "localStorage.getItem" when args is [UiLanguageStartup.StorageKey] => stored,
                "navigator.languages.join" when languagesThrow => throw new JSException("not available"),
                "navigator.languages.join" when args is [","] => languages,
                _ => throw new InvalidOperationException("unexpected JS call " + identifier),
            };
            return ValueTask.FromResult((TValue)answer!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    private static async Task<(string Language, List<string> Stored)> RunAsync(FakeWebView js, bool hasPriorUse = false)
    {
        var stored = new List<string>();
        var language = await UiLanguageStartup.ApplyAsync(js, l => { stored.Add(l); return Task.CompletedTask; }, hasPriorUse);
        return (language, stored);
    }

    [Fact]
    public async Task First_start_in_an_english_browser_is_stored_as_english()
    {
        var (language, stored) = await RunAsync(new(null, "en-US,en"));
        Assert.Equal("en", language);
        Assert.Equal(new[] { "en" }, stored);
    }

    [Fact]
    public async Task First_start_in_a_german_browser_is_german()
    {
        var (language, stored) = await RunAsync(new(null, "de-DE,de,en"));
        Assert.Equal("de", language);
        Assert.Equal(new[] { "de" }, stored);
    }

    [Fact]
    public async Task First_start_in_an_unsupported_language_falls_back_to_english()
    {
        var (language, stored) = await RunAsync(new(null, "ja-JP,zh-CN"));
        Assert.Equal("en", language);
        Assert.Equal(new[] { "en" }, stored);
    }

    [Fact]
    public async Task Stored_choice_is_kept_and_not_written_again()
    {
        var (language, stored) = await RunAsync(new("fr", "en-US"));
        Assert.Equal("fr", language);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task Region_tag_left_by_an_older_version_is_normalised_once()
    {
        var (language, stored) = await RunAsync(new("en-GB", "de"));
        Assert.Equal("en", language);
        Assert.Equal(new[] { "en" }, stored);
    }

    [Fact]
    public async Task Existing_session_without_a_stored_choice_keeps_german_and_stores_it()
    {
        var (language, stored) = await RunAsync(new(null, "en-US"), hasPriorUse: true);
        Assert.Equal("de", language);
        Assert.Equal(new[] { "de" }, stored);
    }

    [Fact]
    public async Task Web_view_without_navigator_languages_gives_english()
    {
        var (language, _) = await RunAsync(new(null, null, languagesThrow: true));
        Assert.Equal("en", language);
    }
}
