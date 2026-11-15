// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// "The network may be back": raised when the platform learns that connectivity returned (or, in the
/// native app, that it came back to the foreground), so pages that could not reach the server re-ask now
/// instead of waiting for their next retry (<see cref="Reasker"/>). The browser registers
/// <see cref="BrowserConnectivitySignal"/> (the <c>online</c> event); the native app shell registers its own.
/// Optional everywhere: a page without a registered signal still re-asks on the retry rhythm.
/// </summary>
public interface IConnectivitySignal
{
    event Action? Restored;

    /// <summary>False while asking the network makes no sense, e.g. the native app is in the background:
    /// <see cref="Reasker"/> then neither asks nor keeps a timer, and continues on the next <see cref="Restored"/>
    /// (the app raises it on resume). Default true, so signals that predate it (and the browser) keep working.</summary>
    bool CanAsk => true;
}

/// <summary>Default registration in the browser client: <c>window</c>'s <c>online</c> event. Listens only
/// while someone is subscribed.</summary>
public sealed class BrowserConnectivitySignal(IJSRuntime js) : IConnectivitySignal, IAsyncDisposable
{
    private DotNetObjectReference<BrowserConnectivitySignal>? _self;
    private Action? _handlers;

    public event Action? Restored
    {
        add
        {
            var first = _handlers is null;
            _handlers += value;
            if (first) _ = ListenAsync();
        }
        remove
        {
            _handlers -= value;
            if (_handlers is null) _ = StopAsync();
        }
    }

    [JSInvokable]
    public void OnOnline() => _handlers?.Invoke();

    private async Task ListenAsync()
    {
        try
        {
            _self ??= DotNetObjectReference.Create(this);
            await js.InvokeVoidAsync("studylifeConnectivity.listen", _self);
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // No JS (prerender, closed circuit): the page keeps its retry rhythm.
        }
    }

    private async Task StopAsync()
    {
        try { await js.InvokeVoidAsync("studylifeConnectivity.stop"); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Nothing to stop without JS.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _handlers = null;
        await StopAsync();
        _self?.Dispose();
    }
}
