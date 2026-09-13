using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/coursegoals domain operations: the upsert (PUT is the only write shape - the route's
/// courseId IS the goal's identity), the delete, and the settings-cache bump plus goal webhook
/// events both of them owe. <see cref="CourseGoalsController"/> keeps only route binding and
/// status-code mapping.
/// </summary>
public interface ICourseGoalService
{
    Task<List<CourseGoalDto>> GetAllAsync();
    Task<ServiceResult<CourseGoalDto>> SaveAsync(int courseId, CourseGoalDto dto);
    Task<ServiceResult> DeleteAsync(int courseId);
}

public class CourseGoalService(
    StudyLifeDb db,
    ICourseResolver courseResolver,
    WebhooksProxyClient webhooks,
    ICurrentUserAccessor currentUser,
    SettingsCacheVersion settingsCacheVersion) : ICourseGoalService
{
    public async Task<List<CourseGoalDto>> GetAllAsync() =>
        await db.CourseGoals.AsNoTracking().Select(g => ToDto(g)).ToListAsync();

    public async Task<ServiceResult<CourseGoalDto>> SaveAsync(int courseId, CourseGoalDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CourseName)) return ServiceResult<CourseGoalDto>.Invalid("CourseName must not be empty.");
        if (dto.Grade is < 1.0m or > 5.0m) return ServiceResult<CourseGoalDto>.Invalid("Grade must be between 1.0 and 5.0.");

        var entity = await db.CourseGoals.FirstOrDefaultAsync(g => g.CourseId == courseId);
        var isNew = entity == null;
        var wasCompletedBefore = entity?.CompletedAt != null;
        if (entity == null)
        {
            // Audit finding M2: a NEW goal binds a fresh CourseId, so it must resolve against
            // the user's full course universe (see CourseResolver) - CourseName is then derived
            // from the resolved course, not taken from the client. An UPDATE of an EXISTING
            // goal, below, never re-validates or re-derives: the route parameter IS the goal's
            // CourseId (there is no way to change it via this endpoint), so frozen-at-creation
            // semantics apply automatically - editing a goal of a since-deleted custom course
            // must keep working, and a later catalog rename must not rewrite it.
            var course = await courseResolver.ResolveAsync(courseId);
            if (course == null) return ServiceResult<CourseGoalDto>.Invalid(CourseValidationMessages.UnknownCourseId(courseId));

            entity = new CourseGoalEntity { CourseId = courseId, CourseName = course.Name };
            db.CourseGoals.Add(entity);
        }
        entity.TargetDate = dto.TargetDate;
        entity.CompletionNote = dto.CompletionNote;
        entity.CompletedAt = dto.CompletedAt;
        entity.Grade = dto.Grade;
        entity.CompletedTopics = dto.CompletedTopics;
        entity.Tag = dto.Tag;
        await db.SaveChangesAsync();
        // Goals feed the cached metrics endpoints (average grade, upcoming goals, topic progress)
        // but have no counter of their own - bumping the settings version is what makes a goal
        // change visible on the next /api/metrics call instead of after the cache TTL.
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);

        var payload = new { courseId = entity.CourseId, courseName = entity.CourseName };
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId,
            isNew ? WebhookEventTypes.CourseGoalCreated : WebhookEventTypes.CourseGoalUpdated,
            payload, CancellationToken.None);
        if (entity.CompletedAt != null && !wasCompletedBefore)
        {
            _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.CourseGoalCompleted,
                payload, CancellationToken.None);
        }
        return ServiceResult<CourseGoalDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int courseId)
    {
        var entity = await db.CourseGoals.FirstOrDefaultAsync(g => g.CourseId == courseId);
        if (entity == null) return ServiceResult.NotFound();
        db.CourseGoals.Remove(entity);
        await db.SaveChangesAsync();
        // Goals feed the cached metrics endpoints (average grade, upcoming goals, topic progress)
        // but have no counter of their own - bumping the settings version is what makes a goal
        // change visible on the next /api/metrics call instead of after the cache TTL.
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.CourseGoalDeleted,
            new { courseId = entity.CourseId, courseName = entity.CourseName }, CancellationToken.None);
        return ServiceResult.Success();
    }

    // internal instead of private: reused by BackupController (JSON export) and every summary
    // endpoint (Dashboard/Metrics/Report/Setup/Stats), so none of them has to duplicate the
    // same mapping again.
    internal static CourseGoalDto ToDto(CourseGoalEntity e) => new()
    {
        CourseId = e.CourseId,
        CourseName = e.CourseName,
        TargetDate = e.TargetDate,
        CompletionNote = e.CompletionNote,
        CompletedAt = e.CompletedAt,
        Grade = e.Grade,
        CompletedTopics = e.CompletedTopics,
        Tag = e.Tag,
    };
}
