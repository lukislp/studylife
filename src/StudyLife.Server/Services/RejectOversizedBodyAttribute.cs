using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StudyLife.Server.Services;

/// <summary>
/// Answers 413 from the declared Content-Length BEFORE model binding runs. [RequestSizeLimit]
/// only bites when Kestrel actually reads past the limit, and the in-action ContentLength check
/// BackupController.ImportJson used as defense in depth now runs AFTER [ApiController] model
/// validation - since the DTOs carry [MaxLength] limits, an oversized note inside an oversized
/// import used to surface as a 400 validation error instead of the honest 413. A resource filter
/// sits in front of binding and validation alike, so the size answer stays 413 regardless of
/// what the body contains.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RejectOversizedBodyAttribute(long maxBytes) : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength is { } length && length > maxBytes)
        {
            // KB for small limits (e.g. the 32 KB telemetry batch guard) instead of always
            // rounding to MB, which used to read as a useless "max 0 MB" below one megabyte.
            var limitText = maxBytes >= 1024 * 1024 ? $"{maxBytes / (1024 * 1024)} MB" : $"{maxBytes / 1024} KB";
            context.Result = new ObjectResult(new { error = $"Request body is too large (max {limitText})." })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
            };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
