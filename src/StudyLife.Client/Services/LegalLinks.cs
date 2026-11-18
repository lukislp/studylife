// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// The legal pages to link: the publisher's privacy policy and imprint, plus the operator's own
/// privacy policy when the server was configured with one (Legal:PrivacyUrl). The one place that
/// decides which address a link gets; <see cref="PrivacyPolicyLink"/> still owns the publisher's
/// privacy addresses.
/// </summary>
public sealed record LegalLinks(string AppPrivacyUrl, string? OperatorPrivacyUrl, string ImprintUrl)
{
    /// <summary>The publisher's imprint on lktec.org.</summary>
    public const string PublisherImprint = "https://lktec.org/impressum";

    /// <summary>True when the operator has a privacy policy of their own: then it and the app's
    /// policy are two separate links.</summary>
    public bool HasOperatorPrivacy => OperatorPrivacyUrl is not null;

    /// <summary>Only the publisher's pages - what every installation without Legal:* shows.</summary>
    public static LegalLinks Publisher(string? language) => Resolve(language, null);

    /// <summary>The server's answer applied to the publisher's defaults. An address that is not a
    /// valid https URL is ignored (the server refuses to start with one, so this only guards a
    /// foreign or tampered answer).</summary>
    public static LegalLinks Resolve(string? language, LegalLinksDto? configured) => new(
        PrivacyPolicyLink.For(language),
        LegalUrl.TryParse(configured?.PrivacyUrl, out var privacy) ? privacy!.AbsoluteUri : null,
        LegalUrl.TryParse(configured?.ImprintUrl, out var imprint) ? imprint!.AbsoluteUri : PublisherImprint);
}

/// <summary>Reads the operator's legal pages from the anonymous GET api/instance the client can call
/// before sign-in. Never throws: any failure (offline, older server, a captive portal's page)
/// yields the publisher's pages, which is the behaviour before the setting existed.</summary>
public sealed class LegalLinksSource(HttpClient http)
{
    public async Task<LegalLinks> LoadAsync(string? language, CancellationToken ct = default)
    {
        try
        {
            var info = await http.GetFromJsonAsync<InstanceInfoDto>("api/instance", StudyLifeJson.Options, ct);
            return LegalLinks.Resolve(language, info?.Legal);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
            or NotSupportedException or InvalidOperationException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return LegalLinks.Publisher(language);
        }
    }
}
