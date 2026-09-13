using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/sessiontemplates domain operations (feature "session templates for quickly creating
/// recurring sessions"). Deliberately no update path for the MVP - delete + recreate is enough,
/// see the task description. <see cref="SessionTemplatesController"/> keeps only route binding
/// and status-code mapping.
/// </summary>
public interface ISessionTemplateService
{
    Task<List<SessionTemplateDto>> GetAllAsync();
    Task<ServiceResult<SessionTemplateDto>> CreateAsync(SessionTemplateDto dto);
    Task<ServiceResult> DeleteAsync(int id);
}

public class SessionTemplateService(
    StudyLifeDb db,
    ICourseResolver courseResolver,
    WebhooksProxyClient webhooks,
    ICurrentUserAccessor currentUser) : ISessionTemplateService
{
    public async Task<List<SessionTemplateDto>> GetAllAsync() =>
        await db.SessionTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => ToDto(t)).ToListAsync();

    public async Task<ServiceResult<SessionTemplateDto>> CreateAsync(SessionTemplateDto dto)
    {
        var error = Validate(dto);
        if (error != null) return ServiceResult<SessionTemplateDto>.Invalid(error);

        // Audit finding M2: no update path exists here (see the interface doc comment), so every
        // template creation is a fresh CourseId binding - CourseName/CourseColor are derived
        // from the resolved course, client-supplied values are ignored.
        var course = await courseResolver.ResolveAsync(dto.CourseId);
        if (course == null) return ServiceResult<SessionTemplateDto>.Invalid(CourseValidationMessages.UnknownCourseId(dto.CourseId));

        var entity = new SessionTemplateEntity
        {
            Name = dto.Name.Trim(),
            CourseId = dto.CourseId,
            CourseName = course.Name,
            CourseColor = course.Color,
            DurationMinutes = dto.DurationMinutes,
            Topic = dto.Topic,
            DefaultWeekday = dto.DefaultWeekday,
            DefaultStartTime = dto.DefaultStartTime,
            CreatedAt = DateTime.UtcNow,
        };
        db.SessionTemplates.Add(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.SessionTemplateCreated,
            new { id = entity.Id, name = entity.Name, courseId = entity.CourseId }, CancellationToken.None);
        return ServiceResult<SessionTemplateDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var entity = await db.SessionTemplates.FindAsync(id);
        if (entity == null) return ServiceResult.NotFound();
        db.SessionTemplates.Remove(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.SessionTemplateDeleted,
            new { id = entity.Id, name = entity.Name }, CancellationToken.None);
        return ServiceResult.Success();
    }

    private static string? Validate(SessionTemplateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "Name must not be empty.";
        if (dto.CourseId <= 0) return "CourseId must be greater than 0.";
        if (string.IsNullOrWhiteSpace(dto.CourseName)) return "CourseName must not be empty.";
        if (dto.DurationMinutes <= 0) return "DurationMinutes must be greater than 0.";
        if (dto.DefaultWeekday is < 0 or > 6) return "DefaultWeekday must be between 0 and 6.";
        return null;
    }

    // internal instead of private: reused by BackupController (JSON export/import), same
    // pattern as SessionService.ToDto.
    internal static SessionTemplateDto ToDto(SessionTemplateEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        CourseId = e.CourseId,
        CourseName = e.CourseName,
        CourseColor = e.CourseColor,
        DurationMinutes = e.DurationMinutes,
        Topic = e.Topic,
        DefaultWeekday = e.DefaultWeekday,
        DefaultStartTime = e.DefaultStartTime,
        CreatedAt = e.CreatedAt,
    };
}
