namespace StudyLife.Server.Configuration;

/// <summary>
/// WebAuthn relying-party overrides. Unset (the normal case) means RP-id and origin are derived
/// from the request itself; the prod ConfigMap pins them so a request that bypasses the gateway
/// cannot move the relying party.
/// </summary>
public sealed class Fido2Options
{
    public const string SectionName = "Fido2";

    public string? ServerDomain { get; set; }

    /// <summary>Env form Fido2__Origins__0, Fido2__Origins__1, ...</summary>
    public string[]? Origins { get; set; }
}

/// <summary>
/// Consent "connect" flow (AuthController.5.Consent.cs / .10.OAuthClients.cs).
/// </summary>
public sealed class ConsentOptions
{
    public const string SectionName = "Consent";

    /// <summary>Flips the generic OAuth-client flow from "PKCE enforced when offered" to
    /// "PKCE mandatory" (2026-09-11 audit, finding 6).</summary>
    public bool RequirePkce { get; set; }

    /// <summary>
    /// Exact additional redirect URIs per audience (env
    /// Consent__AllowedRedirectUris__&lt;audience&gt;__0). Replaces the former dynamically built
    /// configuration key "Consent:AllowedRedirectUris:{audience}": the audience is the DICTIONARY
    /// key now, so no configuration path is assembled from user input any more.
    /// </summary>
    public Dictionary<string, string[]> AllowedRedirectUris { get; set; } = [];
}

/// <summary>Self-registration gate (audit A10). See
/// <see cref="Services.RegistrationGateService.GetMode"/> for why an unrecognized value falls
/// back to the safer "invite" rather than throwing.</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>"open" / "invite" / "closed", case-insensitive; unset or unrecognized = invite.</summary>
    public string? Mode { get; set; }
}

/// <summary>
/// Optional operator-pinned VAPID key pair. When both keys are set they win over the DB-backed
/// pair (<see cref="Services.SystemSecretsService"/>); otherwise this section is simply absent.
/// </summary>
public sealed class VapidOptions
{
    public const string SectionName = "Vapid";

    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }

    /// <summary>Blank falls back to the built-in mailto: subject.</summary>
    public string? Subject { get; set; }
}
