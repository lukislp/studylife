using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Passkey/WebAuthn login (phase 2 of the multi-user overhaul). The cryptographic verification
/// (attestation on registration, assertion on login) is handled entirely by Fido2NetLib -
/// this class only handles orchestration: caching challenges (IMemoryCache, 5 minutes),
/// persisting credentials/sessions, and the "who is actually being registered" decision.
///
/// All /api/auth paths are exempt from both the API key AND session requirement in the gate
/// in Program.cs (you can't authenticate before you're logged in) - the session-required
/// endpoints here (logout, device list, additional passkey) therefore check themselves via
/// HttpContext.Items[AuthSessionService.SessionItemKey] that a REAL validated session
/// is present and not just the shared API key.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _config;
    private readonly SystemSecretsService _systemSecrets;

    public AuthController(StudyLifeDb db, IDistributedCache cache, IConfiguration config, SystemSecretsService systemSecrets)
    {
        _db = db;
        _cache = cache;
        _config = config;
        _systemSecrets = systemSecrets;
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

    /// <summary>Challenge intermediate state of an ongoing registration. ForAuthUserId != null
    /// for "additional passkey for one's own account" (via session OR via link code) - for
    /// open registration, the user decision (claim legacy user vs. create new) is only made
    /// in Complete. RequiresSessionAtComplete distinguishes HOW ForAuthUserId was authorized:
    /// true = via begin-additional (Complete must see the same valid session again),
    /// false = via link code (begin-linked already verified authorization via the code itself -
    /// the calling device has no session at all). LinkCode is only set on the code path, so
    /// Complete can mark it as consumed after success.</summary>
    private sealed record PendingRegistration(CredentialCreateOptions Options, string DisplayName, int? ForAuthUserId, bool RequiresSessionAtComplete, string? LinkCode = null);

    private sealed record PendingLogin(AssertionOptions Options);

    /// <summary>Short-lived link code (Services.Cache, see LinkCodeCacheKey): assigns a
    /// NEW, not-yet-logged-in device to the same account that generated the code -
    /// an alternative to the browser-dependent WebAuthn hybrid transport (QR+Bluetooth), which is
    /// not reliably discoverable on every browser/OS. Each device registers its OWN
    /// local passkey in the process (no cross-device ceremony) - the code only replaces the
    /// "which account does this belong to?" mapping that begin-additional otherwise reads
    /// from the session.</summary>
    private sealed record PendingDeviceLink(int AuthUserId);

    public class PasskeyRegisterCompleteRequest
    {
        public string OptionsId { get; set; } = "";
        public AuthenticatorAttestationRawResponse Response { get; set; } = default!;
    }

    public class PasskeyLoginCompleteRequest
    {
        public string OptionsId { get; set; } = "";
        public AuthenticatorAssertionRawResponse Response { get; set; } = default!;
    }

    // ── Registration (openly accessible) ─────────────────────────────────────

    [HttpPost("register/begin")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBegin([FromBody] PasskeyRegisterBeginRequestDto request)
    {
        var displayName = (request.DisplayName ?? "").Trim();
        if (displayName.Length is 0 or > 100)
            return BadRequest("DisplayName must be between 1 and 100 characters long.");

        // The first registration (not a single passkey exists yet) requires the setup secret
        // generated at startup and printed to the container logs - this prevents anyone who
        // reaches the open registration endpoint before the actual operator from automatically
        // becoming owner (inheriting existing data + backup/restore rights, see
        // BackupController.IsOwnerAsync). Every subsequent registration deliberately stays open
        // (family signup, unchanged). Same predicate as below in RegisterComplete for taking
        // over the legacy user.
        if (!await _db.PasskeyCredentials.AnyAsync() && !await _systemSecrets.ValidateSetupSecretAsync(request.SetupSecret))
            return Unauthorized("Setup code required or invalid - see server logs.");

        return await BeginRegistration(displayName, forAuthUserId: null, requiresSessionAtComplete: true, excludeCredentials: []);
    }

    /// <summary>
    /// Additional passkey for one's OWN, already logged-in account (e.g. a second device) -
    /// unlike register/begin this requires a valid session and attaches the new passkey to the
    /// same AuthUserId, instead of going through the legacy-user/new-user decision. Assumes that
    /// THIS (already logged-in) device itself generates the new passkey - for a physically
    /// DIFFERENT, not-yet-logged-in device see register/begin-linked.
    /// </summary>
    [HttpPost("register/begin-additional")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBeginAdditional()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        // Exclude the user's already registered passkeys so the browser doesn't
        // offer the same authenticator a second time.
        var exclude = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync();

        return await BeginRegistration(user.DisplayName, forAuthUserId: userId, requiresSessionAtComplete: true,
            excludeCredentials: exclude.Select(id => new PublicKeyCredentialDescriptor(id)).ToList());
    }

    /// <summary>
    /// Additional passkey for one's OWN account, initiated from a NEW, not-yet-logged-in
    /// device via a link code (see LinkBegin) - an alternative to the browser-dependent
    /// WebAuthn cross-device/hybrid transport (QR code+Bluetooth), which depending on
    /// browser/OS is not discoverable at all, or only behind a hidden "more options" link.
    /// Deliberately WITHOUT a session requirement (the calling device has none) - the
    /// authorization lives in the code itself, which only an already logged-in device could
    /// generate. Registers its own, LOCAL passkey on this device (no cross-device ceremony);
    /// like begin-additional it ends up PENDING and still requires an explicit approval.
    /// </summary>
    [HttpPost("register/begin-linked")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBeginLinked([FromBody] DeviceLinkRedeemRequestDto request)
    {
        var code = NormalizeLinkCode(request.Code);
        var link = code.Length == 0 ? null : await CacheGetAsync<PendingDeviceLink>(LinkCodeCacheKey(code));
        if (link is null)
            return BadRequest("Link code invalid or expired.");

        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == link.AuthUserId);
        if (user is null) return BadRequest("Link code invalid or expired.");

        var exclude = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == link.AuthUserId)
            .Select(c => c.CredentialId)
            .ToListAsync();

        return await BeginRegistration(user.DisplayName, forAuthUserId: link.AuthUserId, requiresSessionAtComplete: false,
            excludeCredentials: exclude.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(), linkCode: code);
    }

    private async Task<ActionResult<PasskeyBeginResponseDto>> BeginRegistration(
        string displayName, int? forAuthUserId, bool requiresSessionAtComplete,
        IReadOnlyList<PublicKeyCredentialDescriptor> excludeCredentials, string? linkCode = null)
    {
        // The user handle is an opaque random handle ONLY for the browser's WebAuthn dialog.
        // Account resolution at login relies exclusively on the (unique) CredentialId,
        // never on this handle - which is why it doesn't need to be persisted and may
        // differ per registration (see IsUserHandleOwnerOfCredentialIdCallback in login).
        var user = new Fido2User
        {
            Id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            Name = displayName,
            DisplayName = displayName,
        };

        var options = CreateFido2().RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = excludeCredentials,
            // ResidentKey Required = a real passkey discoverable in the authenticator store
            // (Face ID/Touch ID model); the browser itself picks the right credential at
            // login, without the user having to type a name.
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            // No manufacturer attestation needed: we only want the public key, not
            // device certificate chains (which Apple/Google mostly anonymize anyway).
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var optionsId = Guid.NewGuid().ToString("N");
        await CacheSetAsync(RegistrationCacheKey(optionsId),
            new PendingRegistration(options, displayName, forAuthUserId, requiresSessionAtComplete, linkCode), ChallengeLifetime);

        return new PasskeyBeginResponseDto { OptionsId = optionsId, OptionsJson = options.ToJson() };
    }

    [HttpPost("register/complete")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> RegisterComplete([FromBody] PasskeyRegisterCompleteRequest request)
    {
        if (request.Response is null)
            return BadRequest("Registration challenge unknown or expired.");
        var pending = await CacheGetAsync<PendingRegistration>(RegistrationCacheKey(request.OptionsId));
        if (pending is null)
            return BadRequest("Registration challenge unknown or expired.");
        await _cache.RemoveAsync(RegistrationCacheKey(request.OptionsId));

        // Additional-passkey path via begin-additional: the session must still be valid at
        // the time of Complete AND belong to the same user for whom Begin issued the challenge.
        // The begin-linked path (RequiresSessionAtComplete=false) has no session at all -
        // there the link code already verified the authorization in Begin.
        if (pending.ForAuthUserId is int forUserId && pending.RequiresSessionAtComplete && SessionAuthUserId != forUserId)
            return Unauthorized();

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await CreateFido2().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Response,
                OriginalOptions = pending.Options,
                IsCredentialIdUniqueToUserCallback = async (p, ct) =>
                    !await _db.PasskeyCredentials.AnyAsync(c => c.CredentialId == p.CredentialId, ct),
            });
        }
        catch (Fido2VerificationException)
        {
            return BadRequest("Passkey attestation could not be verified.");
        }

        var now = DateTime.UtcNow;

        // User decision wrapped in a transaction, so that two concurrent first registrations
        // don't both claim the same legacy user or create users on top of each other.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        AuthUserEntity targetUser;
        if (pending.ForAuthUserId is int ownUserId)
        {
            targetUser = (await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == ownUserId))!;
        }
        else
        {
            // Core of the first-registration logic: if NOT a single passkey exists yet, meaning
            // nobody has ever registered, then this first registration "claims" the legacy user
            // created by the phase-1 migration ("My Studies") along with all of its existing
            // data - only its DisplayName is updated to the input. Once at least one passkey
            // exists, the legacy user is taken and every further registration creates a
            // brand-new, empty user (open signup, intentional).
            var anyPasskeyExists = await _db.PasskeyCredentials.AnyAsync();
            var legacyUser = anyPasskeyExists
                ? null
                : await _db.AuthUsers.OrderBy(u => u.Id).FirstOrDefaultAsync();

            if (legacyUser is not null)
            {
                legacyUser.DisplayName = pending.DisplayName;
                targetUser = legacyUser;
            }
            else
            {
                targetUser = new AuthUserEntity { DisplayName = pending.DisplayName, CreatedAt = now };
                _db.AuthUsers.Add(targetUser);
                await _db.SaveChangesAsync(); // Id is needed for the credential row
            }
        }

        // Open first registration (claiming the legacy user or a new user): there is no
        // "other" device that could approve it, so it's approved immediately - the user has
        // just proven possession of their authenticator, the same proof as at login. An
        // additional passkey for one's own account, however: PENDING until an already
        // logged-in device consents via the device list (see LoginComplete/ApproveCredential) -
        // otherwise a stolen/replayed session token alone would be enough to permanently plant
        // one's own, independent access method.
        var isAdditionalDevice = pending.ForAuthUserId is not null;
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            AuthUserId = targetUser.Id,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            CreatedAt = now,
            ApprovedAt = isAdditionalDevice ? null : now,
        });

        // Issue a session only directly at the open first registration (instead of forcing a
        // second passkey dialog right after) - on the additional-passkey path it's neither
        // necessary (the device is already logged in) nor permissible (the new passkey isn't
        // approved yet).
        var token = isAdditionalDevice ? null : AuthSessionService.IssueSession(_db, targetUser.Id, now);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        // The link code is single-use: consumed after the first SUCCESSFUL device, so it
        // cannot be redeemed multiple times. Failed/aborted attempts BEFORE this point
        // deliberately leave it untouched (retry from device A possible without a new code).
        if (pending.LinkCode is { } usedCode) await _cache.RemoveAsync(LinkCodeCacheKey(usedCode));

        return new PasskeyCompleteResponseDto { Token = token, DisplayName = targetUser.DisplayName, Pending = isAdditionalDevice };
    }

    // ── Login ────────────────────────────────────────────────────────────────

    [HttpPost("login/begin")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> LoginBegin()
    {
        if (!await _db.PasskeyCredentials.AsNoTracking().AnyAsync())
            return BadRequest("Noch kein Passkey registriert.");

        // AllowedCredentials deliberately EMPTY: registration requires ResidentKey=Required
        // (discoverable credentials), so the browser finds the passkeys matching the RP itself
        // in the OS store, without the server having to tell it the ids - that's exactly what
        // enables the usernameless login screen. A populated list, on the other hand, would
        // expose the credential ids of all registered users to ANY anonymous caller
        // (user-count enumeration with no benefit to the actual login flow).
        var options = CreateFido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = new List<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsId = Guid.NewGuid().ToString("N");
        await CacheSetAsync(LoginCacheKey(optionsId), new PendingLogin(options), ChallengeLifetime);

        return new PasskeyBeginResponseDto { OptionsId = optionsId, OptionsJson = options.ToJson() };
    }

    [HttpPost("login/complete")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> LoginComplete([FromBody] PasskeyLoginCompleteRequest request)
    {
        // Uniformly 401 for anything that isn't a fully valid login - an attacker should not
        // be able to tell whether the challenge, credential, or signature was the problem.
        if (request.Response is null) return Unauthorized();
        var pending = await CacheGetAsync<PendingLogin>(LoginCacheKey(request.OptionsId));
        if (pending is null)
            return Unauthorized();
        await _cache.RemoveAsync(LoginCacheKey(request.OptionsId));

        var credentialId = request.Response.RawId;
        if (credentialId is null || credentialId.Length == 0) return Unauthorized();
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.CredentialId == credentialId);
        if (credential is null) return Unauthorized();

        VerifyAssertionResult result;
        try
        {
            result = await CreateFido2().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Response,
                OriginalOptions = pending.Options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = credential.SignCount,
                // Account resolution relies exclusively on the unique CredentialId (lookup
                // above) - the user handle from registration is an opaque random handle
                // without a persisted binding (see BeginRegistration) and carries no
                // additional authorization information here. Signature/challenge/origin
                // verification is handled entirely by Fido2NetLib beforehand.
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
            });
        }
        catch (Fido2VerificationException)
        {
            return Unauthorized();
        }

        // Replay protection explicitly here, in addition to Fido2NetLib's own check: if the
        // authenticator uses a counter (either value > 0), the new value must be STRICTLY
        // greater - a counter that went backward suggests a cloned key. Apple authenticators
        // always report 0, so the 0/0 case remains allowed.
        if ((result.SignCount != 0 || credential.SignCount != 0) && result.SignCount <= credential.SignCount)
            return Unauthorized();

        // An additional passkey not yet approved via the device list of an already logged-in
        // device: the signature is cryptographically valid (the requester genuinely possesses
        // the private key), but without approval no session may be created from it yet -
        // otherwise approval would be meaningless. Unlike elsewhere, NO generic 401 here: whoever
        // can present a valid signature has already proven possession, so "pending_approval"
        // instead of "wrong" is not an additional information leak for this device, just an
        // honest status response.
        if (credential.ApprovedAt is null)
            return Unauthorized(new { error = "pending_approval" });

        var now = DateTime.UtcNow;
        credential.SignCount = result.SignCount;
        credential.LastUsedAt = now;

        // Opportunistic cleanup instead of a dedicated background job: expired sessions are
        // worthless anyway (Validate rejects them), here they get removed when the chance arises.
        await _db.AuthSessions.Where(s => s.ExpiresAt <= now || s.HardExpiresAt <= now).ExecuteDeleteAsync();

        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == credential.AuthUserId);
        var token = AuthSessionService.IssueSession(_db, credential.AuthUserId, now);
        await _db.SaveChangesAsync();

        return new PasskeyCompleteResponseDto { Token = token, DisplayName = user?.DisplayName ?? "" };
    }

    // ── Native app token handoff (PKCE-style, RFC 7636/8252 §8.1) ────────────────

    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Token bound to the code_challenge given at handoff time - the token itself is
    /// only ever released again to whoever proves possession of the matching code_verifier.</summary>
    private sealed record PendingHandoff(string Token, string CodeChallenge);

    /// <summary>
    /// PKCE handoff (unauthenticated like the rest of /api/auth - the caller has just proven
    /// possession of a real token from register/login/recovery complete, that IS the
    /// authorization here): stores the token server-side under a fresh, single-use code bound
    /// to the app's code_challenge, so a native app shell's studylife:// custom-scheme redirect
    /// (or the Windows loopback listener) only ever has to carry the opaque code, never the
    /// bearer token itself - see AppReturnContext.BuildTokenReturnRedirectAsync for why this
    /// matters (a different app claiming the same custom scheme, or a local process racing the
    /// loopback port, could otherwise turn interception of the redirect into a full account
    /// takeover with no further step needed).
    /// </summary>
    [HttpPost("handoff")]
    public async Task<ActionResult<AuthHandoffResponseDto>> Handoff([FromBody] AuthHandoffRequestDto request)
    {
        if (string.IsNullOrEmpty(request.Token) || string.IsNullOrEmpty(request.CodeChallenge))
            return BadRequest();

        var code = GenerateHandoffCode();
        await CacheSetAsync(HandoffCacheKey(code), new PendingHandoff(request.Token, request.CodeChallenge), HandoffLifetime);
        return new AuthHandoffResponseDto { Code = code };
    }

    /// <summary>
    /// Redeems a handoff code for the real session token - unauthenticated (the app has no
    /// session yet), protected instead by the code being single-use + short-lived + bound to a
    /// code_verifier that only the app which generated the original code_challenge can know
    /// (never transmitted until this exact call). Uniformly 401 for "not found"/"expired"/
    /// "wrong verifier", same non-distinguishing pattern as recovery/login.
    ///
    /// The cache entry is deliberately removed ONLY on a successful match, not on every
    /// attempt: whoever intercepts the code (the exact scenario this whole mechanism defends
    /// against) could otherwise grief the legitimate app's real exchange call by firing one
    /// request with a garbage verifier first - that would burn the single use without ever
    /// producing a token for anyone, a pure denial-of-service with no compensating security
    /// benefit (guessing the correct 256-bit verifier isn't something repeated attempts make
    /// meaningfully more likely). Found live during production testing of this exact flow.
    /// </summary>
    [HttpPost("exchange")]
    public async Task<ActionResult<AuthExchangeResponseDto>> Exchange([FromBody] AuthExchangeRequestDto request)
    {
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
            return Unauthorized();

        var key = HandoffCacheKey(request.Code);
        var pending = await CacheGetAsync<PendingHandoff>(key);
        if (pending is null) return Unauthorized();

        var computedChallenge = Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(request.CodeVerifier)));
        var computedBytes = System.Text.Encoding.ASCII.GetBytes(computedChallenge);
        var expectedBytes = System.Text.Encoding.ASCII.GetBytes(pending.CodeChallenge);
        if (computedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes))
            return Unauthorized();

        await _cache.RemoveAsync(key); // single-use: only ever consumed on the successful redemption
        return new AuthExchangeResponseDto { Token = pending.Token };
    }

    private static string GenerateHandoffCode() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HandoffCacheKey(string code) => $"auth-handoff:{code}";

    // ── Session-required management ────────────────────────────────────────

    /// <summary>
    /// Generates a short-lived link code for one's own account (session-required - only an
    /// already logged-in device may issue one). For register/begin-linked: the code replaces
    /// the account mapping that begin-additional otherwise reads from the session there. The
    /// new device still ends up PENDING after Complete and additionally still requires an
    /// explicit approval via the device list - the code only proves "which account", not
    /// "this is genuinely trustworthy" (see RegisterComplete/LoginComplete).
    /// </summary>
    [HttpPost("link/begin")]
    public async Task<ActionResult<DeviceLinkCodeResponseDto>> LinkBegin()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();

        var displayCode = GenerateLinkCode();
        await CacheSetAsync(LinkCodeCacheKey(displayCode), new PendingDeviceLink(userId), LinkCodeLifetime);
        return new DeviceLinkCodeResponseDto { Code = displayCode, ExpiresInSeconds = (int)LinkCodeLifetime.TotalSeconds };
    }

    // ── Recovery codes (emergency access when a passkey is lost) ────────────────

    private const int RecoveryCodeCount = 8;
    // Without 0/O/1/I - codes are typed in from paper. 12 characters from a 32-char alphabet = 60 bits.
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Generates 8 fresh one-time codes and returns the plaintext EXACTLY ONCE (only hashes
    /// are stored in the DB). Requires a REAL passkey session - a leaked API key must not be
    /// able to construct emergency access for itself (same rationale as for ha-api-key/calendar
    /// token). The user's existing codes are fully invalidated in the process.
    /// </summary>
    [HttpPost("recovery/generate")]
    public async Task<ActionResult<RecoveryCodesResponseDto>> GenerateRecoveryCodes()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();

        await _db.RecoveryCodes.Where(c => c.AuthUserId == userId).ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var codes = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = string.Concat(Enumerable.Range(0, 12)
                .Select(_ => RecoveryCodeAlphabet[RandomNumberGenerator.GetInt32(RecoveryCodeAlphabet.Length)]));
            codes.Add($"{raw[..4]}-{raw[4..8]}-{raw[8..]}");
            _db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                AuthUserId = userId,
                CodeHash = AuthSessionService.HashToken(raw),
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync();
        return new RecoveryCodesResponseDto { Codes = codes };
    }

    /// <summary>Status for the setup card: how many codes are still unused, when they were created.</summary>
    [HttpGet("recovery/status")]
    public async Task<ActionResult<RecoveryStatusDto>> GetRecoveryStatus()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var codes = await _db.RecoveryCodes.AsNoTracking()
            .Where(c => c.AuthUserId == userId).ToListAsync();
        return new RecoveryStatusDto
        {
            TotalCount = codes.Count,
            UnusedCount = codes.Count(c => c.UsedAt == null),
            CreatedAt = codes.Count > 0 ? codes.Max(c => c.CreatedAt) : null,
        };
    }

    /// <summary>
    /// Emergency login with a one-time code (unauthenticated like login/begin - codes are
    /// exactly the way back in without a passkey). The hash identifies the user directly
    /// (unique index); uniformly 401 for both "unknown" and "already used". Brute force is
    /// throttled via its own strict rate-limit partition in Program.cs.
    /// </summary>
    [HttpPost("recovery/login")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> RecoveryLogin([FromBody] RecoveryLoginRequestDto request)
    {
        var normalized = new string((request.Code ?? "")
            .ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length == 0) return Unauthorized();

        var hash = AuthSessionService.HashToken(normalized);
        var code = await _db.RecoveryCodes.FirstOrDefaultAsync(c => c.CodeHash == hash && c.UsedAt == null);
        if (code is null) return Unauthorized();

        var now = DateTime.UtcNow;
        code.UsedAt = now;
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == code.AuthUserId);
        var token = AuthSessionService.IssueSession(_db, code.AuthUserId, now);
        await _db.SaveChangesAsync();
        return new PasskeyCompleteResponseDto { Token = token, DisplayName = user?.DisplayName ?? "" };
    }

    /// <summary>
    /// Client info about one's own account, currently only IsOwner (true for the first
    /// registered user). The client uses this to avoid showing the backup/restore UI
    /// (Setup.razor, Index.razor reminder) to any other user in the first place, instead of
    /// letting them hit a 403 from BackupController.IsOwnerAsync on click.
    /// </summary>
    [HttpGet("account-info")]
    public async Task<ActionResult<AccountInfoDto>> GetAccountInfo()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var firstUserId = await _db.AuthUsers.OrderBy(u => u.Id).Select(u => u.Id).FirstOrDefaultAsync();
        return new AccountInfoDto { IsOwner = userId == firstUserId };
    }

    /// <summary>Server-side invalidation of one's own session ("device lost" case) -
    /// the row is deleted, making the token immediately and permanently worthless.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (HttpContext.Items[AuthSessionService.SessionItemKey] is not int sessionId) return Unauthorized();
        await _db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpGet("credentials")]
    public async Task<ActionResult<List<PasskeyListItemDto>>> ListCredentials()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        return await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyListItemDto
            {
                Id = c.Id,
                DeviceLabel = c.DeviceLabel,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
                Pending = c.ApprovedAt == null,
            })
            .ToListAsync();
    }

    /// <summary>
    /// Approves a passkey created via register/begin-additional that has not yet been
    /// approved - callable ONLY from an already logged-in device of the same account
    /// (SessionAuthUserId must match the target credential). Afterward the new device can log
    /// in normally via login/begin+complete.
    /// </summary>
    [HttpPost("credentials/{id:int}/approve")]
    public async Task<IActionResult> ApproveCredential(int id)
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();
        if (credential.ApprovedAt is not null) return NoContent(); // already approved, idempotent

        credential.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("credentials/{id:int}/label")]
    public async Task<IActionResult> RenameCredential(int id, [FromBody] PasskeyRenameRequestDto request)
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();

        var label = (request.Label ?? "").Trim();
        if (label.Length > 100) return BadRequest("Label must be at most 100 characters long.");
        credential.DeviceLabel = label.Length == 0 ? null : label;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("credentials/{id:int}")]
    public async Task<IActionResult> DeleteCredential(int id)
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();

        // Deleting the last passkey would permanently lock the user out once their sessions
        // expire (there is no password fallback) - deliberately blocked. Only APPROVED
        // passkeys count toward this: a still-pending additional passkey (reject case) is
        // useless for login anyway and must never prevent deletion of the only real access
        // method.
        var ownApprovedCount = await _db.PasskeyCredentials.CountAsync(c => c.AuthUserId == userId && c.ApprovedAt != null);
        if (credential.ApprovedAt is not null && ownApprovedCount <= 1)
            return BadRequest("The last passkey of an account cannot be removed.");

        _db.PasskeyCredentials.Remove(credential);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>AuthUserId of this request's validated session, or null if the request came
    /// without a (valid) X-Session-Token. Both items are set exclusively by the middleware in
    /// Program.cs after successful token validation - the SessionItemKey distinguishes a "real
    /// session" from the API-key fallback resolution, which only sets the user key.</summary>
    private int? SessionAuthUserId =>
        HttpContext.Items.ContainsKey(AuthSessionService.SessionItemKey)
        && HttpContext.Items[CurrentUserAccessor.HttpContextItemKey] is int userId
            ? userId
            : null;

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

    private static string RegistrationCacheKey(string optionsId) => $"passkey-reg:{optionsId}";
    private static string LoginCacheKey(string optionsId) => $"passkey-login:{optionsId}";

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
