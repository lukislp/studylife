using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/notes domain operations: listing, provider-dependent full-text search (see
/// <see cref="INoteSearchStrategy"/>) and the create/update/delete write paths with their
/// CourseId/SessionId resolution and note webhook events. <see cref="NotesController"/> keeps
/// only route binding and status-code mapping.
/// </summary>
public interface INoteService
{
    Task<List<NoteDto>> GetAllAsync();

    /// <summary>Full-text search over title + content; an empty query yields an empty list without
    /// touching the search index at all.</summary>
    Task<List<NoteDto>> SearchAsync(string? query);

    Task<ServiceResult<NoteDto>> CreateAsync(NoteDto dto);
    Task<ServiceResult<NoteDto>> UpdateAsync(int id, NoteDto dto);
    Task<ServiceResult> DeleteAsync(int id);
}

public class NoteService(
    StudyLifeDb db,
    INoteSearchStrategy searchStrategy,
    ICourseResolver courseResolver,
    WebhooksProxyClient webhooks,
    ICurrentUserAccessor currentUser) : INoteService
{
    public async Task<List<NoteDto>> GetAllAsync() =>
        await db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).Select(n => ToDto(n)).ToListAsync();

    public async Task<List<NoteDto>> SearchAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var notes = await searchStrategy.SearchAsync(db, query);
        return notes.Select(ToDto).ToList();
    }

    public async Task<ServiceResult<NoteDto>> CreateAsync(NoteDto dto)
    {
        // A note without a course is legit (e.g. a fresh capture before the AI enrichment or the
        // user assigns one) - only a NON-NULL CourseId/SessionId is validated, same "set means
        // checked" contract as the CourseId re-validation on UpdateAsync below.
        if (dto.CourseId is { } courseId)
        {
            var course = await courseResolver.ResolveAsync(courseId);
            if (course == null) return ServiceResult<NoteDto>.Invalid(CourseValidationMessages.UnknownCourseId(courseId));
        }
        if (dto.SessionId is { } sessionId)
        {
            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId);
            if (!sessionExists) return ServiceResult<NoteDto>.Invalid(SessionValidationMessages.UnknownSessionId(sessionId));
        }

        var entity = new NoteEntity
        {
            Title = dto.Title,
            Content = dto.Content,
            CourseId = dto.CourseId,
            SessionId = dto.SessionId,
            IsMarkdown = dto.IsMarkdown,
            SourceUrl = dto.SourceUrl,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        db.Notes.Add(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.NoteCreated,
            new { noteId = entity.Id, courseId = entity.CourseId, sessionId = entity.SessionId }, CancellationToken.None);
        return ServiceResult<NoteDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult<NoteDto>> UpdateAsync(int id, NoteDto dto)
    {
        var entity = await db.Notes.FindAsync(id);
        if (entity == null) return ServiceResult<NoteDto>.NotFound();

        // Frozen-at-creation exemption, mirroring SessionService.UpdateAsync's CourseId handling
        // (audit finding M2 follow-up): only a CourseId/SessionId that actually CHANGED to a new
        // non-null value is re-validated - editing an old note still bound to a since-deleted
        // custom course (or a session that has since been removed) must keep working. Changing
        // TO null (detaching) never needs validation either way.
        if (dto.CourseId != entity.CourseId && dto.CourseId is { } courseId)
        {
            var course = await courseResolver.ResolveAsync(courseId);
            if (course == null) return ServiceResult<NoteDto>.Invalid(CourseValidationMessages.UnknownCourseId(courseId));
        }
        if (dto.SessionId != entity.SessionId && dto.SessionId is { } sessionId)
        {
            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId);
            if (!sessionExists) return ServiceResult<NoteDto>.Invalid(SessionValidationMessages.UnknownSessionId(sessionId));
        }

        entity.Title = dto.Title;
        entity.Content = dto.Content;
        entity.CourseId = dto.CourseId;
        entity.SessionId = dto.SessionId;
        entity.IsMarkdown = dto.IsMarkdown;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.NoteUpdated,
            new { noteId = entity.Id, courseId = entity.CourseId, sessionId = entity.SessionId }, CancellationToken.None);
        return ServiceResult<NoteDto>.Success(ToDto(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var entity = await db.Notes.FindAsync(id);
        if (entity == null) return ServiceResult.NotFound();
        db.Notes.Remove(entity);
        await db.SaveChangesAsync();
        _ = webhooks.PublishEventAsync(currentUser.AuthUserId, WebhookEventTypes.NoteDeleted,
            new { noteId = entity.Id }, CancellationToken.None);
        return ServiceResult.Success();
    }

    // internal instead of private: reused by BackupController (JSON export) and the summary
    // endpoints (Dashboard/Stats/Wrapped), so none of them has to duplicate the same mapping.
    internal static NoteDto ToDto(NoteEntity e) => new()
    {
        Id = e.Id,
        Title = e.Title,
        Content = e.Content,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        CourseId = e.CourseId,
        SessionId = e.SessionId,
        IsMarkdown = e.IsMarkdown,
        SourceUrl = e.SourceUrl,
        Tags = e.Tags,
        Summary = e.Summary,
        RelatedNoteIds = CommaSeparatedIds.Parse(e.RelatedNoteIds)
    };
}
