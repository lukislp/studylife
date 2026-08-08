namespace StudyLife.Client.Services;

/// <summary>
/// Toolbelt.Blazor.I18nText auto-mutates a page's own text-table fields and re-renders the owning
/// component when the user live-switches language (no page reload) - but any value COMPUTED from
/// those fields once (a random quote pick, a DateTime-based branch, a lookup table built at load
/// time, a value copied onto a separate model/DTO) is a plain copy the library never touches, so
/// it stays stuck in the old language. Call CheckChangedAsync from OnAfterRenderAsync(firstRender:
/// false) and re-derive whatever's stale when it returns true.
/// </summary>
public sealed class I18nLanguageWatcher(Toolbelt.Blazor.I18nText.I18nText i18nText)
{
    private string _lastLang = "";

    public async Task InitAsync() => _lastLang = await i18nText.GetCurrentLanguageAsync();

    public async Task<bool> CheckChangedAsync()
    {
        var lang = await i18nText.GetCurrentLanguageAsync();
        if (lang == _lastLang) return false;
        _lastLang = lang;
        return true;
    }
}
