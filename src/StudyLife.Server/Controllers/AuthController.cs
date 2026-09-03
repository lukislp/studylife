using System.Security.Cryptography;
using Fido2NetLib;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Passkey/WebAuthn login (phase 2 of the multi-user overhaul). The cryptographic verification
/// (attestation on registration, assertion on login) is handled entirely by Fido2NetLib -
/// this class only handles orchestration: caching challenges (IMemoryCache, 5 minutes),
/// persisting credentials/sessions, and the "who is actually being registered" decision.
///
/// Almost every action here carries [AllowAnonymous] - you can't authenticate before you're
/// logged in (audit finding A3: this used to be one blanket "/api/auth is exempt" path-string
/// check in Program.cs; now each action states its own requirement). The session-required
/// endpoints (logout, device list, additional passkey, mcp-connect) are guarded either by
/// [Authorize(Policy = SessionOnly)] or, where the requirement is conditional (RegisterComplete
/// only needs a session on ONE of its paths), by the shared HttpContext.SessionAuthUserId()
/// read. Whoami is the one action with no attribute at all - it needs the default ApiAccess
/// policy (any credential, not just a session), since its whole purpose is reporting back
/// which credential kind actually matched.
/// </summary>
[ApiController]
[Route("api/auth")]
public partial class AuthController : ControllerBase
{
    // Split across concern-partials (AuthController.1.Registration.cs .. AuthController.9.Devices.cs,
    // this file sorting last): the numeric filename prefixes are LOAD-BEARING, not cosmetic.
    // OpenAPI (docs/api/openapi.json) enumerates actions in the controller's metadata order, which
    // the compiler assigns in *compile* order - and Roslyn compiles a type's partial declarations
    // in the order its source files are fed to it, which for this project is plain ordinal filename
    // order ('1'..'9' sort before 'A' in "AuthController.cs"). Renaming or reordering these files
    // reorders docs/api/openapi.json; keep the numbering if you split further, and never rename a
    // numbered file without re-running the build and diffing the spec.

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _config;
    private readonly SystemSecretsService _systemSecrets;
    private readonly IOwnershipService _ownership;
    private readonly IRegistrationGateService _registrationGate;
    private readonly ConsentRedirectPolicy _consentRedirects;

    public AuthController(StudyLifeDb db, IDistributedCache cache, IConfiguration config,
        SystemSecretsService systemSecrets, IOwnershipService ownership, IRegistrationGateService registrationGate,
        ConsentRedirectPolicy consentRedirects)
    {
        _db = db;
        _cache = cache;
        _config = config;
        _systemSecrets = systemSecrets;
        _ownership = ownership;
        _registrationGate = registrationGate;
        _consentRedirects = consentRedirects;
    }

    // WebAuthn challenge cache on IDistributedCache instead of IMemoryCache: with multiple
    // server instances behind a load balancer it is NOT guaranteed that begin/complete
    // are served by the same pod (no sticky routing built) - without this switch
    // register/complete on a different pod than register/begin would simply see "challenge
    // unknown" and the entire login/registration flow would break under scaled operation.
    // Empirically verified: CredentialCreateOptions/AssertionOptions (Fido2NetLib) serialize/
    // deserialize losslessly via System.Text.Json (challenge/User.Id/Rp.Id identical on
    // roundtrip) - no custom JsonConverter needed. In the default mode (in-memory distributed
    // cache, see Program.cs) behavior is identical to before, just with one extra
    // (de)serialization step.
    private static readonly System.Text.Json.JsonSerializerOptions CacheJsonOptions = new();

    private async Task<T?> CacheGetAsync<T>(string key) where T : class
    {
        var bytes = await _cache.GetAsync(key);
        return bytes is null ? null : System.Text.Json.JsonSerializer.Deserialize<T>(bytes, CacheJsonOptions);
    }

    private Task CacheSetAsync<T>(string key, T value, TimeSpan ttl)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, CacheJsonOptions);
        return _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
    }

    /// <summary>Short-lived link code (Services.Cache, see LinkCodeCacheKey): assigns a
    /// NEW, not-yet-logged-in device to the same account that generated the code -
    /// an alternative to the browser-dependent WebAuthn hybrid transport (QR+Bluetooth), which is
    /// not reliably discoverable on every browser/OS. Each device registers its OWN
    /// local passkey in the process (no cross-device ceremony) - the code only replaces the
    /// "which account does this belong to?" mapping that begin-additional otherwise reads
    /// from the session.</summary>
    private sealed record PendingDeviceLink(int AuthUserId);

    // ── Helpers shared across the partials above ─────────────────────────────
    // NOTE: the numeric segment in the sibling files' names (AuthController.1.Registration.cs
    // .. AuthController.9.Devices.cs) is load-bearing, not cosmetic - see the class doc above.

    private static string GenerateHandoffCode() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Configure Fido2NetLib per request: RP-id/origin come from the request itself (already
    /// the real scheme behind nginx via UseForwardedHeaders), optionally overridable via
    /// Fido2:ServerDomain / Fido2:Origins in configuration, in case an operator's host-header
    /// setup differs. The instance is stateless and cheap to build.
    /// </summary>
    private Fido2 CreateFido2()
    {
        var configuredDomain = _config["Fido2:ServerDomain"];
        var configuredOrigins = _config.GetSection("Fido2:Origins").Get<string[]>();
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = string.IsNullOrWhiteSpace(configuredDomain) ? Request.Host.Host : configuredDomain,
            ServerName = "StudyLife",
            Origins = configuredOrigins is { Length: > 0 }
                ? configuredOrigins.ToHashSet()
                : new HashSet<string> { $"{Request.Scheme}://{Request.Host}" },
        });
    }

    private static readonly TimeSpan LinkCodeLifetime = TimeSpan.FromMinutes(10);

    // Crockford Base32 (without I/L/O/U - easily confused when typed in, or forming
    // objectionable words): 32 characters, evenly divisible by 256 (256/32=8), so no modulo
    // bias in GenerateLinkCode.
    private const string LinkCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int LinkCodeLength = 8;

    /// <summary>8 characters from LinkCodeAlphabet (CSPRNG, no modulo bias) formatted as
    /// "XXXX-XXXX" for typing in on a second device - 32^8 ≈ 1.1 trillion combinations plus a
    /// 10-minute expiry make guessing within the validity window practically hopeless.</summary>
    private static string GenerateLinkCode()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(LinkCodeLength);
        var chars = new char[LinkCodeLength];
        for (var i = 0; i < LinkCodeLength; i++)
            chars[i] = LinkCodeAlphabet[bytes[i] % LinkCodeAlphabet.Length];
        var raw = new string(chars);
        return $"{raw[..4]}-{raw[4..]}";
    }

    /// <summary>Case, hyphens, and spaces don't matter when typing it in -
    /// only the alphabet characters themselves count for the cache-key comparison.</summary>
    private static string NormalizeLinkCode(string? code) =>
        new((code ?? "").ToUpperInvariant().Where(c => LinkCodeAlphabet.Contains(c)).ToArray());

    private static string LinkCodeCacheKey(string code) => $"device-link:{NormalizeLinkCode(code)}";
}
