using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // Credentials, sessions and issued add-on keys of the CALLING account - the persistence is
    // AuthAccountService's; these actions resolve the session's own ids (user id, session id,
    // token hash) out of HttpContext and hand them over, because those values exist only
    // because [Authorize(SessionOnly)] put them there.

    /// <summary>The session's user id - guaranteed present by [Authorize(SessionOnly)] on every
    /// action that reads it.</summary>
    private int SessionUserId => HttpContext.SessionAuthUserId()!.Value;

    /// <summary>The id of the session row this very request authenticated with - set for every
    /// session-authenticated request by the authentication handler.</summary>
    private int CurrentSessionId => (int)HttpContext.Items[AuthSessionService.SessionItemKey]!;

    /// <summary>
    /// Client info about one's own account, currently only IsOwner (the explicit
    /// AuthUserEntity.IsOwner flag, see OwnershipService). The client uses this to avoid showing
    /// the backup/restore UI (Setup.razor, Index.razor reminder) to any other user in the first
    /// place, instead of letting them hit a 403 from BackupController.IsOwnerAsync on click.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("account-info")]
    public async Task<ActionResult<AccountInfoDto>> GetAccountInfo()
    {
        var userId = SessionUserId;
        return new AccountInfoDto { IsOwner = await _ownership.IsOwnerAsync(userId), UserId = userId };
    }

    /// <summary>Server-side invalidation of one's own session ("device lost" case) -
    /// the row is deleted, making the token immediately and permanently worthless.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _account.LogoutAsync(CurrentSessionId,
            HttpContext.Items[AuthSessionService.SessionTokenHashItemKey] as string);
        return NoContent();
    }

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("credentials")]
    public async Task<ActionResult<List<PasskeyListItemDto>>> ListCredentials() =>
        await _account.ListCredentialsAsync(SessionUserId);

    /// <summary>
    /// Approves a passkey created via register/begin-additional that has not yet been
    /// approved - callable ONLY from an already logged-in device of the same account
    /// (SessionAuthUserId must match the target credential). Afterward the new device can log
    /// in normally via login/begin+complete.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("credentials/{id:int}/approve")]
    public async Task<IActionResult> ApproveCredential(int id) =>
        (await _account.ApproveCredentialAsync(SessionUserId, id)).ToNoContentResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPut("credentials/{id:int}/label")]
    public async Task<IActionResult> RenameCredential(int id, [FromBody] PasskeyRenameRequestDto request) =>
        (await _account.RenameCredentialAsync(SessionUserId, id, request.Label)).ToNoContentResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("credentials/{id:int}")]
    public async Task<IActionResult> DeleteCredential(int id) =>
        (await _account.DeleteCredentialAsync(SessionUserId, id, CurrentSessionId)).ToNoContentResult(this);

    /// <summary>
    /// "Sign out everywhere else": deletes every session of the account except the one making
    /// this call, so a device the user no longer controls loses access immediately instead of
    /// at its sliding expiry. The current session stays - the user is acting from a device they
    /// evidently still hold.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("sessions/revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        await _account.RevokeOtherSessionsAsync(SessionUserId, CurrentSessionId);
        return NoContent();
    }

    /// <summary>
    /// The add-on keys issued to THIS user via the generic consent flow
    /// (AuthController.10.OAuthClients.cs) - one row per consent click. Joined with the client
    /// registration for a display name; a key whose registration has since been deleted still
    /// lists (with ClientName null) so it can be revoked. Session-only like the other
    /// credential-management endpoints: an API key must never be able to enumerate or revoke
    /// its siblings.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("client-keys")]
    public async Task<ActionResult<List<ClientApiKeyListItemDto>>> ListClientKeys() =>
        await _account.ListClientKeysAsync(SessionUserId);

    /// <summary>Revokes one issued add-on key. The next request carrying it fails
    /// authentication (the handler looks the hash up per request, nothing is cached).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("client-keys/{id:int}")]
    public async Task<IActionResult> RevokeClientKey(int id) =>
        (await _account.RevokeClientKeyAsync(SessionUserId, id)).ToNoContentResult(this);
}
