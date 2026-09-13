using Microsoft.AspNetCore.Mvc;

namespace StudyLife.Server.Services;

/// <summary>
/// The three shapes a domain operation in <c>Services/</c> can end in, expressed without any
/// reference to HTTP: the services own the load/mutate/persist/side-effect part of a request,
/// the controllers own the translation into status codes (see
/// <see cref="ServiceResultExtensions"/>). Deliberately not an exception-based design - "the row
/// you asked for isn't there" and "your input is wrong" are ordinary outcomes of these
/// operations, not exceptional ones, and throwing for them would both cost a stack walk on a
/// perfectly normal 404 and route them through the ProblemDetails handler, which would change
/// the response bodies these endpoints have always returned.
/// </summary>
public enum ServiceOutcome
{
    /// <summary>The operation ran; controllers answer 200/204 (or hand back the value).</summary>
    Success,
    /// <summary>The addressed row does not exist (or belongs to another user, which the global
    /// query filters make indistinguishable); controllers answer 404.</summary>
    NotFound,
    /// <summary>The input failed a domain rule; <c>Error</c> carries the exact, stable message the
    /// endpoint has always returned, and controllers answer 400 with it.</summary>
    Invalid,
    /// <summary>A supplied precondition no longer holds and the CURRENT state is handed back so
    /// the caller can rebase; controllers answer 409 with that value. Only the settings PUT's
    /// optional Version check produces this - see UserSettingsDto.Version.</summary>
    Conflict,
    /// <summary>The authenticated user id no longer resolves to a row; controllers answer 401.
    /// Reachable only from the session-gated endpoints, which carry a user id that WAS valid
    /// when the request was authenticated - a deleted account mid-request, not a missing
    /// credential (the gate has already run by then).</summary>
    Unauthorized,
}

/// <summary>Outcome of a domain operation that produces no payload (deletes, revokes).</summary>
public readonly record struct ServiceResult(ServiceOutcome Outcome, string? Error)
{
    public static ServiceResult Success() => new(ServiceOutcome.Success, null);
    public static ServiceResult NotFound() => new(ServiceOutcome.NotFound, null);
    public static ServiceResult Invalid(string error) => new(ServiceOutcome.Invalid, error);
    public static ServiceResult Unauthorized() => new(ServiceOutcome.Unauthorized, null);
}

/// <summary>Outcome of a domain operation that produces a DTO on success.</summary>
public readonly record struct ServiceResult<T>(ServiceOutcome Outcome, T? Value, string? Error)
{
    public static ServiceResult<T> Success(T value) => new(ServiceOutcome.Success, value, null);
    public static ServiceResult<T> NotFound() => new(ServiceOutcome.NotFound, default, null);
    public static ServiceResult<T> Invalid(string error) => new(ServiceOutcome.Invalid, default, error);

    /// <summary>Carries the CURRENT state, not the rejected input - that is the whole point of
    /// the 409 body.</summary>
    public static ServiceResult<T> Conflict(T current) => new(ServiceOutcome.Conflict, current, null);

    public static ServiceResult<T> Unauthorized() => new(ServiceOutcome.Unauthorized, default, null);
}

/// <summary>
/// The controller half of <see cref="ServiceResult"/>: one mapping, used by every endpoint, so a
/// "not found" can never accidentally become a 400 in one controller and a 404 in the next.
/// Three variants rather than one, because the endpoints' declared return types are part of the
/// committed OpenAPI contract (docs/api/openapi.json) and must not change: an action typed
/// <c>ActionResult&lt;T&gt;</c> returns the bare value, one typed <c>IActionResult</c> wraps it in
/// <c>Ok(...)</c>, and a delete answers <c>NoContent()</c> - exactly as each did before.
/// </summary>
public static class ServiceResultExtensions
{
    /// <summary>For actions declared as <c>Task&lt;ActionResult&lt;T&gt;&gt;</c>.</summary>
    public static ActionResult<T> ToActionResult<T>(this ServiceResult<T> result, ControllerBase controller) =>
        result.Outcome switch
        {
            ServiceOutcome.NotFound => controller.NotFound(),
            ServiceOutcome.Invalid => controller.BadRequest(result.Error),
            ServiceOutcome.Conflict => controller.Conflict(result.Value),
            ServiceOutcome.Unauthorized => controller.Unauthorized(),
            _ => result.Value!,
        };

    /// <summary>For actions declared as <c>Task&lt;IActionResult&gt;</c> that answered <c>Ok(dto)</c>.</summary>
    public static IActionResult ToOkResult<T>(this ServiceResult<T> result, ControllerBase controller) =>
        result.Outcome switch
        {
            ServiceOutcome.NotFound => controller.NotFound(),
            ServiceOutcome.Invalid => controller.BadRequest(result.Error),
            ServiceOutcome.Conflict => controller.Conflict(result.Value),
            ServiceOutcome.Unauthorized => controller.Unauthorized(),
            _ => controller.Ok(result.Value),
        };

    /// <summary>For actions declared as <c>Task&lt;IActionResult&gt;</c> that answered <c>NoContent()</c>.</summary>
    public static IActionResult ToNoContentResult(this ServiceResult result, ControllerBase controller) =>
        result.Outcome switch
        {
            ServiceOutcome.NotFound => controller.NotFound(),
            ServiceOutcome.Invalid => controller.BadRequest(result.Error),
            ServiceOutcome.Unauthorized => controller.Unauthorized(),
            _ => controller.NoContent(),
        };
}
