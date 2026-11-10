// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// Start-up wiring of <see cref="UiLanguage"/>: reads the stored choice and the device languages
/// from the web view, asks the rule, and stores the answer the same way a manual choice is stored
/// (through the I18nText library's own persistence, so the next start finds it as a stored
/// choice). Shared by the browser client (Program.cs) and the native app shell (AppRoot), so both
/// decide identically. Uses only JS identifiers that exist in every web view, hence no function
/// in interop.js that the native app would have to mirror.
/// </summary>
public static class UiLanguageStartup
{
    /// <summary>The key the I18nText library persists the current language under.</summary>
    public const string StorageKey = "Toolbelt.Blazor.I18nText.CurrentLanguage";

    /// <summary>
    /// Decides the language of this start, stores it when it differs from what is stored, and
    /// returns it. <paramref name="setLanguage"/> is <c>I18nText.SetCurrentLanguageAsync</c>;
    /// <paramref name="hasPriorUse"/> is "a session token already exists on this device".
    /// </summary>
    public static async Task<string> ApplyAsync(IJSRuntime js, Func<string, Task> setLanguage, bool hasPriorUse)
    {
        var stored = await TryAsync<string?>(js, "localStorage.getItem", StorageKey);
        var language = UiLanguage.Resolve(stored, await GetDeviceLanguagesAsync(js), hasPriorUse);
        if (!string.Equals(stored, language, StringComparison.Ordinal))
            await setLanguage(language);
        return language;
    }

    /// <summary>The device or browser languages in preference order: <c>navigator.languages</c>,
    /// which in the native app is the list of system languages the web view reports. The JS
    /// interop binds <c>join</c> to its parent array, like <c>localStorage.getItem</c> above.
    /// A web view without the property yields an empty list and so the English fallback.</summary>
    internal static async Task<string[]> GetDeviceLanguagesAsync(IJSRuntime js)
    {
        var list = await TryAsync<string?>(js, "navigator.languages.join", ",");
        return (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<T?> TryAsync<T>(IJSRuntime js, string identifier, params object?[] args)
    {
        try { return await js.InvokeAsync<T>(identifier, args); }
        catch { return default; }
    }
}
