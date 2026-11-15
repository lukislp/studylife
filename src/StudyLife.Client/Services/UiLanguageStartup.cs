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
    /// Decides the language of this start, tells the library, stores it when it differs from what is
    /// stored, and returns it. <paramref name="setLanguage"/> is <c>I18nText.SetCurrentLanguageAsync</c>;
    /// <paramref name="hasPriorUse"/> is "a session token already exists on this device";
    /// <paramref name="prescribed"/> is the language the host prescribes (see <see cref="UiLanguage.Resolve"/>).
    /// </summary>
    public static async Task<string> ApplyAsync(IJSRuntime js, Func<string, Task> setLanguage, bool hasPriorUse, string? prescribed = null)
    {
        var stored = await TryAsync<string?>(js, "localStorage.getItem", StorageKey);
        var language = UiLanguage.Resolve(stored, await GetDeviceLanguagesAsync(js), hasPriorUse, prescribed);
        // Always told to the library, also when it equals the stored choice: the library's own current
        // language is not necessarily initialised from storage (in the native app shell it stayed empty,
        // so GetCurrentLanguageAsync returned "" and read aloud sent ?lang=). The library returns early
        // when it already shows this language, so the call is free in that case.
        await setLanguage(language);
        if (!string.Equals(stored, language, StringComparison.Ordinal))
        {
            // Written here as well: the library skips its own persistence when the language it detected
            // from navigator.languages already equals this one, so nothing was stored and the next start
            // counted as "no stored choice". In the native app that next start comes right after the first
            // server address is saved, where an install with a server and no stored choice is treated as
            // an old install and switched to German (the English iPad of 2026-11-14).
            await TryWriteAsync(js, language);
        }
        return language;
    }

    /// <summary>
    /// The current UI language as a <see cref="StudyLife.Shared.SupportedLanguages"/> code, never empty:
    /// what the library reports, else the stored choice, else the device languages, else English.
    /// For every consumer that sends the language to the server or to a speech engine; the library
    /// alone can report an empty string in a host that never set it.
    /// <paramref name="getLibraryLanguage"/> is <c>I18nText.GetCurrentLanguageAsync</c>.
    /// </summary>
    public static async Task<string> GetCurrentCodeAsync(IJSRuntime js, Func<Task<string>> getLibraryLanguage)
    {
        string? reported = null;
        try { reported = await getLibraryLanguage(); }
        catch { /* fall through to the stored choice */ }
        if (UiLanguage.Match(reported) is { } known) return known;
        var stored = await TryAsync<string?>(js, "localStorage.getItem", StorageKey);
        return UiLanguage.Resolve(stored, await GetDeviceLanguagesAsync(js));
    }

    private static async Task TryWriteAsync(IJSRuntime js, string language)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, language); }
        catch { /* storage not available: this start still shows the language */ }
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
