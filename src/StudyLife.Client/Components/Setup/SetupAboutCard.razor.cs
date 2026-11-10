// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.I18nText;
using StudyLife.Client.Services;

namespace StudyLife.Client.Components.Setup;

/// <summary>Code-behind of the About card: wiring and the few strings it composes. What the versions
/// and links are is decided by <see cref="AboutSource"/>, where the notices come from by
/// <see cref="AboutState"/>.</summary>
public partial class SetupAboutCard : ComponentBase, IDisposable
{
    /// <summary>The SPDX id of StudyLife's own licence. Licence names are not translated.</summary>
    public const string LicenceName = "AGPL-3.0-only";

    public const string LicenceUrl = "https://www.gnu.org/licenses/agpl-3.0.html";

    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private IClientPlatform Platform { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>Texts to render with. Left null in the app (loaded from the i18n table for the
    /// current language); component tests pass them in directly.</summary>
    [Parameter] public AboutText? Text { get; set; }

    /// <summary>The state to read from; left null in the app (built from the injected services),
    /// component tests pass one with fake HTTP and platform.</summary>
    [Parameter] public AboutState? State { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private AboutText? T;
    private AboutState? _state;
    private IReadOnlyList<AboutBuild> _builds = [];
    private AboutNotices? _notices;
    private bool _open;
    private bool _loading;
    private bool _noticesRequested;

    protected override async Task OnInitializedAsync()
    {
        T = Text ?? await Services.GetRequiredService<Toolbelt.Blazor.I18nText.I18nText>()
            .GetTextTableAsync<AboutText>(this);
        _state = State ?? new AboutState(Http, Platform);
        _builds = await _state.LoadBuildsAsync(_cts.Token);
    }

    protected override void OnParametersSet()
    {
        if (Text is not null) T = Text;
    }

    /// <summary>Opens or closes the notices; the text is fetched on the first opening only.</summary>
    private async Task ToggleNoticesAsync()
    {
        _open = !_open;
        if (!_open || _noticesRequested) return;

        _noticesRequested = true;
        _loading = true;
        try
        {
            _notices = await _state!.LoadNoticesAsync(_cts.Token);
        }
        finally
        {
            _loading = false;
        }
    }

    private string BuildLabel(AboutBuild build) => build.Kind switch
    {
        AboutBuildKind.App => T!.AppLabel,
        AboutBuildKind.Browser => T!.BrowserLabel,
        _ => T!.ServerLabel,
    };

    private string VersionText(AboutBuild build) =>
        build.Version is { Length: > 0 } v ? $"v{v}" : T!.Unknown;

    private string SourceTitle(AboutBuild build) => build.Source.IsExact ? T!.SourceExact : T!.SourceRepo;

    private string SourceAriaLabel(AboutBuild build) =>
        $"{T!.SourceLink}: {BuildLabel(build)} {VersionText(build)}, {SourceTitle(build)} ({T.NewTab})";

    private string LicenceAriaLabel => $"{T!.LicenceLabel}: {LicenceName} ({T.NewTab})";

    private string ExpandedValue => _open ? "true" : "false";

    private bool Collapsed => !_open;

    private string ToggleLabel => _open ? T!.HideNotices : T!.ShowNotices;

    private string OriginText => _notices?.Origin == AboutNoticesOrigin.App ? T!.FromApp : T!.FromServer;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
