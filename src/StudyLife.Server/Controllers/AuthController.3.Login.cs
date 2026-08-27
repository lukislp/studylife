using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    private sealed record PendingLogin(AssertionOptions Options);

    public class PasskeyLoginCompleteRequest
    {
        public string OptionsId { get; set; } = "";
        public AuthenticatorAssertionRawResponse Response { get; set; } = default!;
    }

    // ── Login ────────────────────────────────────────────────────────────────

    [AllowAnonymous]
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

    [AllowAnonymous]
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

    private static string LoginCacheKey(string optionsId) => $"passkey-login:{optionsId}";
}
