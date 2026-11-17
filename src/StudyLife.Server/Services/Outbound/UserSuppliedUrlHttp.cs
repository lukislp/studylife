// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace StudyLife.Server.Services.Outbound;

/// <summary>
/// The one way the server builds an HttpClient for a URL that came from a USER rather than from the
/// operator's configuration (2026-11-16 audit M4). Today that is the Web Push endpoint a browser
/// hands PushService; any future feature that fetches or posts to a user-given URL uses the same
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

    /// <summary>
    /// The primary handler. Public so a component that owns its HttpClient (for example a shared
    /// Web Push transport) can build on exactly the same vetted connect instead of its own.
    /// </summary>
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
    public HttpClient Http { get; }

    public UserSuppliedUrlHttpClient(IHostAddressResolver resolver, IOutboundAddressFilter filter) =>
        Http = new HttpClient(UserSuppliedUrlHttp.CreateHandler(resolver, filter), disposeHandler: true)
        {
            Timeout = UserSuppliedUrlHttp.RequestTimeout,
        };

    /// <summary>Production resolver and filter.</summary>
    public UserSuppliedUrlHttpClient() : this(DnsHostAddressResolver.Instance, PublicAddressFilter.Instance) { }

    public void Dispose() => Http.Dispose();
}
