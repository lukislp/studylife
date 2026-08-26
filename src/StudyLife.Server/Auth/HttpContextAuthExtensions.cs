using StudyLife.Server.Services;

namespace StudyLife.Server.Auth;

/// <summary>
/// ONE shared implementation of the "AuthUserId of this request's real passkey session, or
/// null" read that used to be copy-pasted as a private property in AuthController
/// (SessionAuthUserId), SettingsController (SessionUser), SystemController (SessionAuthUserId),
/// and AiProxyController (SessionUser) - audit finding A3. Actions guarded by
/// [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)] no longer need to call this
/// defensively (the policy already rejected anything that isn't a real session before the
/// action runs), so most call sites now use it as a pure, non-null read; a couple of call sites
/// with additional conditional logic (e.g. AuthController.RegisterComplete, which only
/// sometimes requires a session) still use it as the nullable check it always was.
/// </summary>
public static class HttpContextAuthExtensions
{
    /// <summary>AuthUserId of this request's validated session, or null if the request came
    /// without a (valid) X-Session-Token - i.e. HttpContext.Items[AuthSessionService.
    /// SessionItemKey] is unset (a bare API key/calendar token never sets it).</summary>
    public static int? SessionAuthUserId(this HttpContext context) =>
        context.Items.ContainsKey(AuthSessionService.SessionItemKey)
        && context.Items[CurrentUserAccessor.HttpContextItemKey] is int userId
            ? userId
            : null;
}
