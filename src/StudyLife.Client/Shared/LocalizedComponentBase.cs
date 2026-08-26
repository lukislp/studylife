using Microsoft.AspNetCore.Components;

namespace StudyLife.Client.Shared;

/// <summary>
/// Base class for pages/components that render localized text via Toolbelt.Blazor.I18nText's
/// generated per-page text tables (e.g. I18nText.IndexText). Centralizes the "T = await
/// I18nText.GetTextTableAsync&lt;TTable&gt;(this)" load that used to be hand-copied into every
/// component's own OnInitializedAsync (MainLayout.razor.OnInitializedAsync is the smallest
/// still-manual example of that shape) - AND, more importantly, closes the gap it left open: for
/// the one render Blazor performs after OnInitializedAsync's synchronous prefix but before its
/// first await resolves, T used to be a freshly-constructed TTable with every string property
/// null. Every string.Format(T.SomeKey, ...) call in the codebase had to defensively write
/// "T.SomeKey ?? """ to survive that split second (string.Format throws ArgumentNullException on
/// a null format string) - easy to forget, and an audit found ~18 call sites across 6 pages that
/// had (StudyLife audit hygiene sweep).
///
/// A component derived from this class never sees that gap: wrap its ENTIRE markup in
/// "@if (IsTextLoaded) { ... }" (the exact shape MainLayout.razor already uses for its own
/// "@if (_authenticated)" gate around @Body) so that unavoidable pre-load render produces no
/// output at all instead of touching T. IsTextLoaded flips to true - and stays true - the instant
/// T finishes loading, strictly before any markup the "@if" gates ever gets a chance to run, so
/// every T.SomeKey access downstream of the gate is guaranteed non-null. This doesn't change
/// WHEN a page's content first becomes visible: like today, that still only happens once the
/// derived class's full load sequence (T plus whatever else OnTextLoadedAsync awaits) completes -
/// see this class's remarks for the lifecycle contract that keeps that timing identical.
/// </summary>
/// <remarks>
/// Do not override OnInitializedAsync directly - it's sealed here. Instead:
/// <list type="bullet">
/// <item>Override <see cref="OnInitializingAsync"/> for anything that should start alongside the
/// text-table fetch (event subscriptions, kicking off other independent requests to keep - not
/// regress - the "start every task immediately, await once" pattern already used by
/// Index.razor.cs/Stats.razor.cs/Setup.razor/Focus.razor). T is still the default, empty instance
/// here - don't read it.</item>
/// <item>Override <see cref="OnTextLoadedAsync"/> for everything else - T is guaranteed loaded
/// and non-null there. This is where most of a converted component's original
/// OnInitializedAsync body ends up, close to verbatim.</item>
/// </list>
/// </remarks>
public abstract class LocalizedComponentBase<TTable> : ComponentBase
    where TTable : class, Toolbelt.Blazor.I18nText.Interfaces.I18nTextFallbackLanguage, new()
{
    [Inject] protected Toolbelt.Blazor.I18nText.I18nText I18nText { get; set; } = default!;

    /// <summary>The active language's text table. Only ever read after <see cref="IsTextLoaded"/>
    /// is true (i.e. from <see cref="OnTextLoadedAsync"/> onward, or from markup behind an
    /// "@if (IsTextLoaded)" gate) - see the class doc for why that's guaranteed.</summary>
    protected TTable T { get; private set; } = new();

    /// <summary>True once <see cref="T"/> is fully loaded. Gate a derived component's entire
    /// markup on this (see class doc) instead of guarding individual string.Format calls.</summary>
    protected bool IsTextLoaded { get; private set; }

    protected sealed override async Task OnInitializedAsync()
    {
        var textTask = I18nText.GetTextTableAsync<TTable>(this);
        await OnInitializingAsync();
        T = await textTask;
        IsTextLoaded = true;
        await OnTextLoadedAsync();
    }

    /// <summary>Runs before the text table has loaded, alongside the text-table fetch itself - see
    /// class remarks. Do not read <see cref="T"/> here.</summary>
    protected virtual Task OnInitializingAsync() => Task.CompletedTask;

    /// <summary>Runs once <see cref="T"/> is loaded and guaranteed non-null - see class remarks.</summary>
    protected virtual Task OnTextLoadedAsync() => Task.CompletedTask;
}
