// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Auth;

/// <summary>
/// Marks an action that a session may only run after it confirmed its user moments ago
/// (ISessionStepUpService, POST api/auth/account/reauth). Refuses with 403 and the stable problem type
/// <see cref="ProblemType"/> otherwise, which the client answers by showing the shared
/// "confirm it's you" step (FreshProofPicker) and retrying. 403, not 401: the session is valid,
/// and the client treats 401 as "signed out".
///
/// Runs as an action filter: after the authorization policy of the action (SessionOnly), before
/// the action body. The raw backup endpoints check ownership inside the action, so a stale
/// non-owner session gets this 403 instead of the plain one - a refusal either way. A request
/// without a session (an API key) never counts as confirmed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStepUpAttribute : Attribute, IAsyncActionFilter
{
    public const string ProblemType = "step-up-required";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var confirmed = http.Items[AuthSessionService.SessionItemKey] is int sessionId
            && http.Items[AuthSessionService.SessionTokenHashItemKey] is string tokenHash
            && await http.RequestServices.GetRequiredService<ISessionStepUpService>()
                .IsConfirmedAsync(sessionId, tokenHash, http.RequestAborted);
        if (confirmed)
        {
            await next();
            return;
        }

        var problems = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = problems.CreateProblemDetails(http, StatusCodes.Status403Forbidden, title: ProblemType,
            type: ProblemType, detail: "Confirm it is you first (POST api/auth/account/reauth), then repeat the request.");
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
