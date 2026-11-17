// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Options;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Configuration;

/// <summary>
/// Startup validation (ValidateOnStart) of the named <see cref="AddonPurgeOptions"/>: a set BaseUrl
/// must be an absolute http(s) URL, and <see cref="AddonTlsOptions.RequireHttps"/> applies to it
/// unless the deployment exempts that add-on (<see cref="AddonPurgeOptions.AllowPlainHttp"/>).
/// A BaseUrl without a SharedSecret is deliberately NOT a startup error: the purges are then queued
/// and retried until the secret arrives (<see cref="AddonPurgeClient"/>), so a deployment can ship
/// the URL before the secret without either crashing or dropping a purge.
/// </summary>
public sealed class AddonPurgeOptionsValidator(IOptions<AddonTlsOptions> tls) : IValidateOptions<AddonPurgeOptions>
{
    public ValidateOptionsResult Validate(string? name, AddonPurgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (AddonPurgeTarget.ForName(name) is not { } target) return ValidateOptionsResult.Skip;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) return ValidateOptionsResult.Success;

        var setting = $"{target.SectionPath}:BaseUrl";
        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ValidateOptionsResult.Fail($"{setting} must be an absolute http:// or https:// URL.");

        if (options.AllowPlainHttp) return ValidateOptionsResult.Success;
        try
        {
            AddonTlsConfiguration.EnsureAllowedScheme(tls.Value, setting, options.BaseUrl);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(
                $"{ex.Message} Set {target.SectionPath}:AllowPlainHttp only for an add-on that does not serve TLS yet.");
        }
    }
}
