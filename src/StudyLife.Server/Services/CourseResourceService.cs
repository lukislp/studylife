using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/courseresources domain operations (setup page, CourseResourcesModal.razor).
/// Deliberately no update path - delete + recreate is enough for the manageable number of
/// entries per course. <see cref="CourseResourcesController"/> keeps only route binding and
/// status-code mapping.
/// </summary>
public interface ICourseResourceService
{
    Task<List<CourseResourceDto>> GetByCourseAsync(int courseId);
    Task<ServiceResult<CourseResourceDto>> CreateAsync(CourseResourceDto dto);
    Task<ServiceResult> DeleteAsync(int id);
}

public class CourseResourceService(
    StudyLifeDb db,
    ICourseResolver courseResolver,
    WebhooksProxyClient webhooks,
    ICurrentUserAccessor currentUser) : ICourseResourceService
{
    public async Task<List<CourseResourceDto>> GetByCourseAsync(int courseId) =>
        await db.CourseResources.AsNoTracking()
            .Where(r => r.CourseId == courseId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => ToDto(r))
            .ToListAsync();

    public async Task<ServiceResult<CourseResourceDto>> CreateAsync(CourseResourceDto dto)
    {
        var error = Validate(dto);
        if (error != null) return ServiceResult<CourseResourceDto>.Invalid(error);

        // Audit finding M2: unlike Sessions/CourseGoals/SessionTemplates, CourseResourceEntity
        // has no CourseName/CourseColor of its own to derive - just the existence check against
        // the user's full course universe (see CourseResolver). No update path exists here (see
        // the interface doc comment), so there is no "unchanged CourseId" exemption to apply.
        if (await courseResolver.ResolveAsync(dto.CourseId) == null)
            return ServiceResult<CourseResourceDto>.Invalid(CourseValidationMessages.UnknownCourseId(dto.CourseId));

        var entity = new CourseResourceEntity
        {
            CourseId = dto.CourseId,
            Title = dto.Title.Trim(),
            Url = dto.Url.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.CourseResources.Add(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.CourseResourceCreated,
            new { id = entity.Id, courseId = entity.CourseId, title = entity.Title, url = entity.Url }, CancellationToken.None);
        return ServiceResult<CourseResourceDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var entity = await db.CourseResources.FindAsync(id);
        if (entity == null) return ServiceResult.NotFound();
        db.CourseResources.Remove(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.CourseResourceDeleted,
            new { id = entity.Id, courseId = entity.CourseId }, CancellationToken.None);
        return ServiceResult.Success();
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
    // as SessionService.ToDto.
    internal static CourseResourceDto ToDto(CourseResourceEntity e) => new()
    {
        Id = e.Id,
        CourseId = e.CourseId,
        Title = e.Title,
        Url = e.Url,
        CreatedAt = e.CreatedAt,
    };
}
