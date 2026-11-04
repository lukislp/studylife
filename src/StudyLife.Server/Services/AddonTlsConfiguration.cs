// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Security;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Builds the HttpClients for the three add-on proxies (<see cref="AiProxyClient"/>,
/// <see cref="WebhooksProxyClient"/>, <see cref="DeveloperProxyClient"/>) from
/// <see cref="AddonTlsOptions"/>. The decision table, kept out of Program.cs so it is testable:
/// <list type="bullet">
/// <item>Nothing configured (the default): a plain <c>new HttpClient()</c>, exactly as before.
/// http:// URLs stay plain HTTP, https:// URLs are validated against the system trust store.</item>
/// <item><see cref="AddonTlsOptions.CaCertificatePath"/> set: the CA file is loaded once at
/// startup (missing or empty file = startup error) and <see cref="PrivateCaCertificateValidator"/>
/// validates every https:// add-on connection against it, host name included. http:// URLs are
/// unaffected, so the CA can be mounted before the add-ons serve TLS.</item>
/// <item><see cref="AddonTlsOptions.RequireHttps"/> true: every configured add-on URL must be
/// https://, otherwise startup fails. That is the switch to flip once all add-ons serve TLS, so
/// a stale http:// URL can never send the shared secrets in plain text again.</item>
/// </list>
/// There is intentionally no switch that turns certificate validation off. See
/// docs/SECURITY-ASSURANCE.md, section 8.3.
/// </summary>
public static class AddonTlsConfiguration
{
    /// <summary>The validation callback for <see cref="AddonTlsOptions.CaCertificatePath"/>, or
    /// null when no CA is configured (default validation applies).</summary>
    public static RemoteCertificateValidationCallback? CreateValidationCallback(AddonTlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.CaCertificatePath))
            return null;
        var roots = PrivateCaCertificateValidator.LoadTrustedRoots(options.CaCertificatePath.Trim());
        return PrivateCaCertificateValidator.CreateCallback(roots);
    }

    /// <summary>
    /// An HttpClient for one add-on. Without a callback it is a plain <c>new HttpClient()</c> (the
    /// previous behaviour); with one, a SocketsHttpHandler that validates server certificates
    /// through it. <paramref name="timeout"/> overrides the HttpClient default when given.
    /// </summary>
    public static HttpClient CreateHttpClient(RemoteCertificateValidationCallback? validationCallback, TimeSpan? timeout = null)
    {
        HttpClient client;
        if (validationCallback is null)
        {
            client = new HttpClient();
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                // Pick up rotated certificates (cert-manager renews well before expiry) on new
                // connections instead of keeping one TLS session forever.
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            };
            handler.SslOptions.RemoteCertificateValidationCallback = validationCallback;
            client = new HttpClient(handler, disposeHandler: true);
        }
        if (timeout is { } t)
            client.Timeout = t;
        return client;
    }

    /// <summary>
    /// With <see cref="AddonTlsOptions.RequireHttps"/> on, throws when <paramref name="url"/> is
    /// set but not an absolute https:// URL. Unset URLs are fine: that add-on is simply off.
    /// </summary>
    public static void EnsureAllowedScheme(AddonTlsOptions options, string setting, string? url)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.RequireHttps || string.IsNullOrWhiteSpace(url))
            return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"{setting} must be an https:// URL while {AddonTlsOptions.SectionName}:RequireHttps is true.");
    }
}

/// <summary>
/// Startup validation (ValidateOnStart) of <see cref="AddonTlsOptions"/>: a configured CA file
/// must load, and with RequireHttps every configured add-on URL must be https://. Turns both into
/// a failed start with a clear message instead of a broken integration found later.
/// </summary>
public sealed class AddonTlsOptionsValidator(
    IOptions<StudyLifeAiOptions> ai,
    IOptions<StudyLifeWebhooksOptions> webhooks,
    IOptions<StudyLifeDevelopersOptions> developers) : IValidateOptions<AddonTlsOptions>
{
    public ValidateOptionsResult Validate(string? name, AddonTlsOptions options)
    {
        var failures = new List<string>();
        try
        {
            _ = AddonTlsConfiguration.CreateValidationCallback(options);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            failures.Add($"{AddonTlsOptions.SectionName}:CaCertificatePath: {ex.Message}");
        }

        var urls = new (string Setting, string? Url)[]
        {
            ($"{StudyLifeAiOptions.SectionName}:BaseUrl", ai.Value.BaseUrl),
            ($"{StudyLifeAiOptions.SectionName}:InternalBaseUrl", ai.Value.InternalBaseUrl),
            ($"{StudyLifeWebhooksOptions.SectionName}:BaseUrl", webhooks.Value.BaseUrl),
            ($"{StudyLifeDevelopersOptions.SectionName}:BaseUrl", developers.Value.BaseUrl),
        };
        foreach (var (setting, url) in urls)
        {
            try
            {
                AddonTlsConfiguration.EnsureAllowedScheme(options, setting, url);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex.Message);
            }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// The add-on certificate validation callback, created once per process from
/// <see cref="AddonTlsOptions"/> (null = default validation) and shared by the three proxy clients.
/// </summary>
public sealed class AddonTlsTrust(IOptions<AddonTlsOptions> options)
{
    private readonly Lazy<RemoteCertificateValidationCallback?> _callback =
        new(() => AddonTlsConfiguration.CreateValidationCallback(options.Value));

    public RemoteCertificateValidationCallback? ValidationCallback => _callback.Value;

    public HttpClient CreateHttpClient(TimeSpan? timeout = null) =>
        AddonTlsConfiguration.CreateHttpClient(ValidationCallback, timeout);
}
