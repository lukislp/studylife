// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using System.Reflection;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>Where the notices a user is looking at came from.</summary>
public enum AboutNoticesOrigin
{
    /// <summary>The notices bundled into this native app build.</summary>
    App,
    /// <summary>The notices the server publishes next to the web build.</summary>
    Server,
}

public sealed record AboutNotices(string Text, AboutNoticesOrigin Origin);

/// <summary>
/// What the About section in Setup shows: the versions of the software that is running, where its
/// source is, and the third-party notices of the build in front of the user. Created by the card
/// that shows it (it needs only the HttpClient and the platform), not registered in DI: the native
/// apps compile this client into their bundle but build their own service container, so a new
/// registration here would have to be mirrored there (same reason as <see cref="AiConsentState"/>).
///
/// Read-only and the same in demo mode: it only fetches public build metadata and a static file.
/// </summary>
public sealed class AboutState
{
    /// <summary>The notices file, published by the server next to the web build (wwwroot) from the
    /// repository's THIRD-PARTY-NOTICES.md (see tools/check-licences.py).</summary>
    public const string ServerNoticesPath = "THIRD-PARTY-NOTICES.md";

    /// <summary>Upper bound for the notices text. The real file is around 40 KB; this only stops a
    /// misconfigured server from feeding the page something absurd.</summary>
    public const int MaxNoticesLength = 1_000_000;

    private const string NoticesTitle = "# Third-party notices";

    private readonly HttpClient _http;
    private readonly IClientPlatform _platform;
    private readonly string? _browserVersion;

    public AboutState(HttpClient http, IClientPlatform platform, string? browserVersion = null)
    {
        _http = http;
        _platform = platform;
        _browserVersion = browserVersion ?? BrowserVersionFromAssembly();
    }

    /// <summary>The informational version of this client build, e.g. "3.26.5+0123abc...".</summary>
    public static string? BrowserVersionFromAssembly() =>
        typeof(AboutState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>The builds to list. An unreachable server still yields the row, with an unknown version.</summary>
    public async Task<IReadOnlyList<AboutBuild>> LoadBuildsAsync(CancellationToken ct = default)
    {
        string? server = null;
        try
        {
            // Cache buster like the update check in MainLayout: the response has no cache headers.
            var dto = await _http.GetFromJsonAsync<VersionResponseDto>(
                $"api/system/version?nocache={DateTime.UtcNow.Ticks}", StudyLifeJson.Options, ct);
            server = dto?.Version;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or an older server: the server row shows "unknown".
        }

        return AboutSource.Compose(server, _browserVersion, _platform.AppVersion, IsNativeApp);
    }

    /// <summary>
    /// The notices of the build the user is running: in a native app the file bundled into the app
    /// (so they match the binary on the device), otherwise, and when the app cannot provide them,
    /// the server's. Null when neither could be read.
    /// </summary>
    public async Task<AboutNotices?> LoadNoticesAsync(CancellationToken ct = default)
    {
        if (IsNativeApp)
        {
            string? bundled = null;
            try { bundled = await _platform.ReadBundledNoticesAsync(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Fall through to the server's copy.
            }
            if (IsNoticesText(bundled)) return new AboutNotices(bundled!, AboutNoticesOrigin.App);
        }

        try
        {
            // The static file is served with revalidation; the SPA fallback answers an unknown path
            // with index.html (HTTP 200), which IsNoticesText rejects.
            var text = await _http.GetStringAsync(ServerNoticesPath, ct);
            if (IsNoticesText(text)) return new AboutNotices(text, AboutNoticesOrigin.Server);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline or an older server without the file.
        }

        return null;
    }

    /// <summary>
    /// True for the notices file: starts with its title and is of sane size. Rejects the SPA's
    /// index.html (served for unknown paths), error pages and anything else that is not our file.
    /// </summary>
    public static bool IsNoticesText(string? text) =>
        text is { Length: > 0 and <= MaxNoticesLength }
        && text.TrimStart((char)0xFEFF).StartsWith(NoticesTitle, StringComparison.Ordinal);

    private bool IsNativeApp => !string.Equals(_platform.Name, "web", StringComparison.Ordinal);
}
