using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// CRUD for the course resource collection (setup page, CourseResourcesModal.razor).
/// Deliberately no PUT/update - delete + recreate is enough for the manageable number of
/// entries per course.
/// </summary>
[ApiController]
[Route("api/courseresources")]
public class CourseResourcesController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly ICourseResolver _courseResolver;
    private readonly WebhooksProxyClient _webhooks;
    private readonly ICurrentUserAccessor _currentUser;

    public CourseResourcesController(StudyLifeDb db, ICourseResolver courseResolver,
        WebhooksProxyClient webhooks, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _courseResolver = courseResolver;
        _webhooks = webhooks;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IEnumerable<CourseResourceDto>> GetByCourse([FromQuery] int courseId) =>
        await _db.CourseResources.AsNoTracking()
            .Where(r => r.CourseId == courseId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => ToDto(r))
            .ToListAsync();

    [HttpPost]
    public async Task<ActionResult<CourseResourceDto>> Create(CourseResourceDto dto)
    {
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        // Audit finding M2: unlike Sessions/CourseGoals/SessionTemplates, CourseResourceEntity
        // has no CourseName/CourseColor of its own to derive - just the existence check against
        // the user's full course universe (see CourseResolver). No PUT/update exists here (see
        // the class doc comment), so there is no "unchanged CourseId" exemption to apply.
        if (await _courseResolver.ResolveAsync(dto.CourseId) == null)
            return BadRequest(CourseValidationMessages.UnknownCourseId(dto.CourseId));

        var entity = new CourseResourceEntity
        {
            CourseId = dto.CourseId,
            Title = dto.Title.Trim(),
            Url = dto.Url.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        _db.CourseResources.Add(entity);
        await _db.SaveChangesAsync();
        _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.CourseResourceCreated,
            new { id = entity.Id, courseId = entity.CourseId, title = entity.Title, url = entity.Url }, CancellationToken.None);
        return ToDto(entity);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.CourseResources.FindAsync(id);
        if (entity == null) return NotFound();
        _db.CourseResources.Remove(entity);
        await _db.SaveChangesAsync();
        _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.CourseResourceDeleted,
            new { id = entity.Id, courseId = entity.CourseId }, CancellationToken.None);
        return NoContent();
    }

    private static string? Validate(CourseResourceDto dto)
    {
        if (dto.CourseId <= 0) return "CourseId must be greater than 0.";
        if (string.IsNullOrWhiteSpace(dto.Title)) return "Title must not be empty.";
        // Server-side counterpart to the maxlength attributes in CourseResourcesModal.razor -
        // those are purely client-side and trivially bypassable via a direct API call.
        if (dto.Title.Trim().Length > 120) return "Title must be at most 120 characters long.";
        if (dto.Url.Trim().Length > 2048) return "Url must be at most 2048 characters long.";
        // Only a plausibility check (absolute http/https URL) - no reachability check, see the task description.
        if (!Uri.TryCreate(dto.Url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Url must be a valid http(s) address.";
        }
        return null;
    }

    // internal instead of private: reused by BackupController (JSON export), same pattern
    // as SessionsController.ToDto.
    internal static CourseResourceDto ToDto(CourseResourceEntity e) => new()
    {
        Id = e.Id,
        CourseId = e.CourseId,
        Title = e.Title,
        Url = e.Url,
        CreatedAt = e.CreatedAt,
    };
}
