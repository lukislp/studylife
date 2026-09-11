using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// Loads the localized weekday/month names for the active UI language from the browser
/// (wwwroot/js/localdate.js) and hands them to <see cref="LocalDate"/>. Kept separate from
/// LocalDate itself so the formatting logic stays free of JS-interop types and is unit-testable.
///
/// Called once before the first render (Program.cs) and again on every live language switch
/// (MainLayout). Results are cached per language: switching back and forth costs no further
/// interop, and re-applying an unchanged set raises no redundant re-render.
/// </summary>
public sealed class LocalDateNames(IJSRuntime jsRuntime)
{
    private readonly Dictionary<string, DateNames> _cache = [];
    private IJSObjectReference? _module;

    public async Task EnsureLoadedAsync(string language)
    {
        if (_cache.TryGetValue(language, out var cached))
        {
            LocalDate.ApplyNames(language, cached);
            return;
        }

        try
        {
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/localdate.js");
            var names = await _module.InvokeAsync<DateNames>("dateNames", language);
            _cache[language] = names;
            LocalDate.ApplyNames(language, names);
        }
        catch
        {
            // JS interop/module not available (prerendering, an installed PWA serving a stale
            // asset list) - LocalDate keeps its invariant English names, which is exactly the
            // behaviour before this existed.
            LocalDate.Language = language;
        }
    }
}
