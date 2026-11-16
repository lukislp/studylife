// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// <see cref="IFileDownloadService"/> for the browser and the installed PWA: the bytes go to
/// wwwroot/js/file-download.js as a stream, which turns them into a blob URL and clicks a
/// temporary &lt;a download&gt;. Every download needs this route because the /api gate wants the
/// session token, which only the HttpClient attaches - a plain link would arrive unauthenticated,
/// and the encrypted database export is a POST with the password in the body anyway.
/// </summary>
public sealed class BrowserFileDownloadService(IJSRuntime js) : IFileDownloadService, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task SaveAsync(FileDownload file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/file-download.js");
        using var stream = new MemoryStream(file.Content, writable: false);
        using var streamRef = new DotNetStreamReference(stream);
        await _module.InvokeVoidAsync("saveFile", cancellationToken, file.FileName, streamRef);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { /* the page is gone, nothing to release */ }
    }
}
