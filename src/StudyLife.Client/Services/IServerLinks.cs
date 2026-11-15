// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// Builds absolute URLs that leave the app: links for other people or devices (invites, the
/// read-only progress link, the calendar subscription). In-app navigation keeps using
/// NavigationManager - this is only for addresses that are shown, copied or sent elsewhere.
/// Why not NavigationManager.BaseUri: in the native app's BlazorWebView that is the virtual local
/// host (app://0.0.0.1/), never an address anyone else can open. The shared HttpClient is always
/// pointed at the real server (the browser's own base address, the native app's configured server
/// URL), so a sub-path install keeps its path and the native app needs no extra implementation.
/// </summary>
public interface IServerLinks
{
    /// <summary>The server's public base address, always ending in "/".</summary>
    Uri BaseAddress { get; }

    /// <summary>Resolves a path relative to the base (no leading slash, query allowed) to an absolute URL.</summary>
    string Absolute(string relativePath);
}

public sealed class HttpServerLinks : IServerLinks
{
    public HttpServerLinks(HttpClient http)
    {
        var baseAddress = http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is not set.");
        // Without a trailing slash the last path segment of a sub-path install would be replaced
        // when resolving a relative path.
        BaseAddress = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
    }

    public Uri BaseAddress { get; }

    public string Absolute(string relativePath) => new Uri(BaseAddress, relativePath.TrimStart('/')).AbsoluteUri;
}
