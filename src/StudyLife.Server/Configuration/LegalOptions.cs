// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Options;
using StudyLife.Shared;

namespace StudyLife.Server.Configuration;

/// <summary>
/// The operator's own legal pages (env <c>Legal__PrivacyUrl</c>, <c>Legal__ImprintUrl</c>). Both are
/// optional: unset means the client keeps linking the publisher's privacy policy and imprint. They
/// reach the client through GET /api/instance. See docs/operator-privacy-template.md.
/// </summary>
public sealed class LegalOptions
{
    public const string SectionName = "Legal";

    /// <summary>The privacy policy of whoever runs this server.</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>The imprint (legal notice) of whoever runs this server.</summary>
    public string? ImprintUrl { get; set; }

    /// <summary>The value to publish for a setting: trimmed, null when unset.</summary>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Refuses a set but unusable address at startup, so a typo never silently falls back to
/// the publisher's page (which would show the wrong party as responsible).</summary>
public sealed class LegalOptionsValidator : IValidateOptions<LegalOptions>
{
    public ValidateOptionsResult Validate(string? name, LegalOptions options)
    {
        var failures = new List<string>();
        Check(failures, "Legal:PrivacyUrl", options.PrivacyUrl);
        Check(failures, "Legal:ImprintUrl", options.ImprintUrl);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string key, string? value)
    {
        if (LegalOptions.Normalize(value) is not { } v || LegalUrl.IsValid(v)) return;
        failures.Add($"{key} '{v}' is not an absolute https URL without credentials "
            + "(http is accepted for localhost only), e.g. https://example.org/privacy. Fix it or leave it unset "
            + "to link the publisher's page.");
    }
}
