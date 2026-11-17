// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace StudyLife.Server.Services.Outbound;

/// <summary>
/// The one way the server builds an HttpClient for a URL that came from a USER rather than from the
/// operator's configuration (2026-11-16 audit M4). Today that is the Web Push endpoint a browser
/// hands PushService, delivered by WebPushDelivery; any future feature that fetches or posts to a user-given URL uses the same
/// client. Every connection goes through <see cref="VettedConnector"/> (resolve, vet, connect to the
/// vetted address only), redirects are never followed (a 3xx from a public host must not lead to a
/// private one), no system proxy is used (a proxy would make the vetted connect pointless) and the
/// limits are short.
///
/// URLs the OPERATOR configures - the add-on base URLs (StudyLifeAi, Webhooks, Developers), the
/// mDNS instance URL, APNs, the Kubernetes API - deliberately do NOT use this client: they point
/// into the cluster on purpose and are trusted configuration, not input. Their clients stay as they
/// are (AddonTlsTrust and friends).
/// </summary>
public static class UserSuppliedUrlHttp
{
    /// <summary>Whole request, including the answer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Name resolution plus TCP connect (and TLS) of one connection.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The primary handler. <see cref="UserSuppliedUrlHttpClient"/> holds the one shared
    /// instance; consumers get clients on it through <see cref="UserSuppliedUrlHttpClient.CreateClient"/>.</summary>
    public static SocketsHttpHandler CreateHandler(IHostAddressResolver resolver, IOutboundAddressFilter filter)
    {
        var connector = new VettedConnector(resolver, filter);
        return new SocketsHttpHandler
        {
            ConnectCallback = connector.ConnectAsync,
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false,
            UseProxy = false,
            // New connections re-resolve and re-vet; a long-lived pooled connection would otherwise
            // keep talking to whatever the name pointed at when it was opened.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>The production handler: system DNS, public addresses only.</summary>
    public static SocketsHttpHandler CreateHandler() =>
        CreateHandler(DnsHostAddressResolver.Instance, PublicAddressFilter.Instance);

    /// <summary>Registers <see cref="UserSuppliedUrlHttpClient"/> together with its resolver and filter.
    /// Tests replace <see cref="IOutboundAddressFilter"/> or <see cref="IHostAddressResolver"/> to reach a local fake.</summary>
    public static IServiceCollection AddUserSuppliedUrlHttpClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IHostAddressResolver>(DnsHostAddressResolver.Instance);
        services.TryAddSingleton<IOutboundAddressFilter>(PublicAddressFilter.Instance);
        services.TryAddSingleton<UserSuppliedUrlHttpClient>();
        return services;
    }
}

/// <summary>
/// The shared, long-lived HttpClient for user-supplied URLs (a singleton; see
/// <see cref="UserSuppliedUrlHttp"/>). One instance means one connection pool; the handler's
/// PooledConnectionLifetime keeps re-resolving and re-vetting.
/// </summary>
public sealed class UserSuppliedUrlHttpClient : IDisposable
{
    private readonly SocketsHttpHandler _handler;

    /// <summary>Client with the default <see cref="UserSuppliedUrlHttp.RequestTimeout"/>.</summary>
    public HttpClient Http { get; }

    public UserSuppliedUrlHttpClient(IHostAddressResolver resolver, IOutboundAddressFilter filter)
    {
        _handler = UserSuppliedUrlHttp.CreateHandler(resolver, filter);
        Http = CreateClient(UserSuppliedUrlHttp.RequestTimeout);
    }

    /// <summary>Production resolver and filter.</summary>
    public UserSuppliedUrlHttpClient() : this(DnsHostAddressResolver.Instance, PublicAddressFilter.Instance) { }

    /// <summary>
    /// A client with its own request timeout on the SAME vetted handler (one connection pool, one
    /// connect path). For a consumer such as WebPushDelivery that needs a different timeout; it
    /// does not own the handler, so disposing it leaves the shared handler alive.
    /// </summary>
    public HttpClient CreateClient(TimeSpan requestTimeout) =>
        new(_handler, disposeHandler: false) { Timeout = requestTimeout };

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
    }
}
