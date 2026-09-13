using System.Reflection;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The two /api/backup operations that touch the database through EF rather than the SQLite
/// file: the human-readable JSON export, and its counterpart - the full-replace JSON import -
/// plus the LastBackupDownloadAt stamp both raw download endpoints owe.
/// <see cref="BackupController"/> keeps everything that is genuinely transport or
/// authorization: the 501 Postgres gate, the owner check, the uploaded/downloaded file
/// plumbing, and the serialization of the export DTO with the framework's own
/// JsonSerializerOptions. Deliberately named apart from <see cref="DatabaseBackupService"/>,
/// which is the raw-file half (online backup API, integrity_check, staging).
/// </summary>
public interface IBackupDataService
{
    /// <summary>
    /// Updates UserSettingsEntity.LastBackupDownloadAt - shared by both raw download paths, so
    /// each is equally hooked up to the dashboard reminder feature (Index.razor).
    /// </summary>
    Task TouchLastBackupDownloadAsync();

    /// <summary>Builds the "v2" export document; the caller serializes and sends it.</summary>
    Task<BackupExportDto> BuildExportAsync();

    /// <summary>
    /// Imports a JSON export as a FULL REPLACE of the calling user's own data - see
    /// <see cref="BackupDataService.ImportJsonAsync"/> for the id-remapping rules and the
    /// tolerance for unresolvable references. <paramref name="authUserId"/> is the session's
    /// user (the endpoint is [Authorize(SessionOnly)]), needed only for the settings-cache bump.
    /// </summary>
    Task<ServiceResult<BackupImportResponseDto>> ImportJsonAsync(BackupExportDto import, int authUserId);
}

public class BackupDataService(StudyLifeDb db, SettingsCacheVersion settingsCacheVersion) : IBackupDataService
{
    public async Task TouchLastBackupDownloadAsync()
    {
        var settingsEntity = await db.Settings.GetOrCreateAsync(db);
        settingsEntity.LastBackupDownloadAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(settingsEntity.AuthUserId);
    }

    /// <summary>
    /// Human-readable JSON export of the actual user data ("v2" format, audit finding M4 - was
    /// previously incomplete: only 5 of the now 9 user-owned tables, and serialized with a
    /// manually constructed JsonSerializerOptions without a naming policy, so the nested DTOs
    /// came out PascalCase instead of matching the rest of the API's camelCase wire format - see
    /// docs/ARCHITECTURE.md). Deliberately still excluded: PushSubscriptions (this browser's
    /// endpoint registrations, not transferable user data), SentReminders (internal dedup
    /// bookkeeping), TimerState (transient live state of the focus timer), and every auth/
    /// infrastructure table (AuthUsers/PasskeyCredentials/AuthSessions/RecoveryCodes/
    /// SystemSecrets/AiKeyOutbox - not user data, and re-importing a session/passkey/setup-code
    /// row would be either meaningless across accounts or a security hole). Uses the same ToDto
    /// projections as the respective aggregate services (NoteService/CourseGoalService/
    /// SessionService/SettingsService/CourseResourceService/SessionTemplateService), plus three
    /// export-only DTOs for the tables that have no id-carrying API DTO of their own
    /// (StudyProgramExportDto/CourseGroupExportDto/CustomCourseExportDto - see their doc comments),
    /// so the export format doesn't drift from the normal API beyond what round-tripping requires.
    /// </summary>
    public async Task<BackupExportDto> BuildExportAsync()
    {
        var sessions = await db.Sessions.AsNoTracking()
            .Select(s => SessionService.ToDto(s)).ToListAsync();
        var notes = await db.Notes.AsNoTracking()
            .OrderByDescending(n => n.UpdatedAt)
            .Select(n => NoteService.ToDto(n)).ToListAsync();
        var courseGoals = await db.CourseGoals.AsNoTracking()
            .Select(g => CourseGoalService.ToDto(g)).ToListAsync();
        var courseResources = await db.CourseResources.AsNoTracking()
            .OrderBy(r => r.CreatedAt)
            .Select(r => CourseResourceService.ToDto(r)).ToListAsync();
        var settingsEntity = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
        var settings = SettingsService.ToDto(settingsEntity ?? new UserSettingsEntity());
        var studyPrograms = await db.StudyPrograms.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new StudyProgramExportDto
            {
                Id = p.Id,
                Name = p.Name,
                CreatedAt = p.CreatedAt,
                IsCompleted = p.IsCompleted,
            }).ToListAsync();
        var courseGroups = await db.CourseGroups.AsNoTracking()
            .Select(g => new CourseGroupExportDto
            {
                Id = g.Id,
                StudyProgramId = g.StudyProgramId,
                Name = g.Name,
                EctsQuota = g.EctsQuota,
            }).ToListAsync();
        var customCourses = await db.CustomCourses.AsNoTracking()
            .OrderBy(c => c.Semester).ThenBy(c => c.Id)
            .Select(c => new CustomCourseExportDto
            {
                Id = c.Id,
                StudyProgramId = c.StudyProgramId,
                Semester = c.Semester,
                Name = c.Name,
                Code = c.Code,
                Color = c.Color,
                Icon = c.Icon,
                Ects = c.Ects,
                CourseGroupId = c.CourseGroupId,
                Topics = c.Topics,
            }).ToListAsync();
        var sessionTemplates = await db.SessionTemplates.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => SessionTemplateService.ToDto(t)).ToListAsync();

        return new BackupExportDto
        {
            FormatVersion = 2,
            ExportedAt = DateTime.UtcNow,
            AppVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "dev",
            Sessions = sessions,
            Notes = notes,
            CourseGoals = courseGoals,
            CourseResources = courseResources,
            Settings = settings,
            StudyPrograms = studyPrograms,
            CourseGroups = courseGroups,
            CustomCourses = customCourses,
            SessionTemplates = sessionTemplates,
        };
    }


    /// <summary>
    /// How many rows the import stages before flushing them (see InsertInBatchesAsync). The
    /// four id-mapped tables used to do one SaveChangesAsync - i.e. one round trip and, on
    /// SQLite, one WAL commit - PER ROW, which for a multi-year account is tens of thousands of
    /// them. Batching is possible because a single SaveChangesAsync populates the generated Id
    /// of EVERY entity it inserts, not just of one (the notes pass below already relied on
    /// that). 500 is a compromise, not a tuned number: large enough that the per-round-trip
    /// cost stops dominating, small enough that neither the change tracker nor the provider's
    /// parameter batching has to hold a whole 64 MB import's worth of rows at once.
    /// </summary>
    private const int ImportBatchSize = 500;

    /// <summary>
    /// Inserts <paramref name="rows"/> in <see cref="ImportBatchSize"/>-sized batches, calling
    /// <paramref name="onInserted"/> for each row right after the batch containing it has been
    /// saved - at which point the entity carries its new database id, which is what the
    /// import's id maps are built from. Each saved batch is then detached: nothing later in the
    /// import touches these entities again (only their ids, already copied out), so leaving
    /// them tracked would just make every subsequent SaveChangesAsync of the same import walk
    /// over more and more unchanged rows. Everything still runs inside ImportJsonAsync's single
    /// transaction, so a failure anywhere rolls back every batch that came before it.
    /// </summary>
    private async Task InsertInBatchesAsync<TRow>(
        IReadOnlyList<TRow> rows, Func<TRow, object> selectEntity, Action<TRow>? onInserted = null)
    {
        for (var start = 0; start < rows.Count; start += ImportBatchSize)
        {
            var end = Math.Min(start + ImportBatchSize, rows.Count);
            for (var i = start; i < end; i++) db.Add(selectEntity(rows[i]));
            await db.SaveChangesAsync();
            for (var i = start; i < end; i++)
            {
                onInserted?.Invoke(rows[i]);
                db.Entry(selectEntity(rows[i])).State = EntityState.Detached;
            }
        }
    }

    public async Task<ServiceResult<BackupImportResponseDto>> ImportJsonAsync(BackupExportDto import, int authUserId)
    {
        // 0 = no formatVersion property in the file at all (legacy v1 - handled below by simply
        // leaving the newer collections at their empty-list default). Anything else that isn't
        // the current version is a file this server version doesn't understand.
        if (import.FormatVersion is not (0 or 2))
            return ServiceResult<BackupImportResponseDto>.Invalid($"Unsupported export formatVersion {import.FormatVersion}.");

        var imported = new Dictionary<string, int>();
        var dropped = new Dictionary<string, int>();
        void Drop(string key) => dropped[key] = dropped.GetValueOrDefault(key) + 1;

        const int offset = StudyProgramCatalog.CustomCourseIdOffset;

        await using var transaction = await db.Database.BeginTransactionAsync();

        // ── Full replace: delete every row this user owns in every exported table ───────────
        // (the global query filters in StudyLifeDb.OnModelCreating already scope every one of
        // these DbSets to the caller, so this can never touch another user's data - no
        // IgnoreQueryFilters() anywhere here, unlike DemoSeeder's table-wide wipe). Settings is
        // a singleton-per-user row (unique index on AuthUserId): deleted and freshly re-inserted
        // below like everything else, instead of the usual GetOrCreateAsync upsert.
        await db.Sessions.ExecuteDeleteAsync();
        await db.Notes.ExecuteDeleteAsync();
        await db.CourseGoals.ExecuteDeleteAsync();
        await db.CourseResources.ExecuteDeleteAsync();
        await db.SessionTemplates.ExecuteDeleteAsync();
        await db.CustomCourses.ExecuteDeleteAsync();
        await db.CourseGroups.ExecuteDeleteAsync();
        await db.StudyPrograms.ExecuteDeleteAsync();
        await db.Settings.ExecuteDeleteAsync();

        // ── Study programs ───────────────────────────────────────────────────────────────────
        var programIdMap = new Dictionary<int, int>();
        var programRows = import.StudyPrograms
            .Select(dto => (dto.Id, Entity: new StudyProgramEntity
            {
                Name = dto.Name,
                CreatedAt = dto.CreatedAt,
                IsCompleted = dto.IsCompleted,
            }))
            .ToList();
        await InsertInBatchesAsync(programRows, r => r.Entity, r => programIdMap[r.Id] = r.Entity.Id);
        imported["studyPrograms"] = programIdMap.Count;

        // ── Elective groups ──────────────────────────────────────────────────────────────────
        var groupIdMap = new Dictionary<int, int>();
        var groupRows = new List<(int OldId, CourseGroupEntity Entity)>();
        foreach (var dto in import.CourseGroups)
        {
            if (!programIdMap.TryGetValue(dto.StudyProgramId, out var newProgramId)) { Drop("courseGroups"); continue; }
            groupRows.Add((dto.Id, new CourseGroupEntity { StudyProgramId = newProgramId, Name = dto.Name, EctsQuota = dto.EctsQuota }));
        }
        await InsertInBatchesAsync(groupRows, r => r.Entity, r => groupIdMap[r.OldId] = r.Entity.Id);
        imported["courseGroups"] = groupIdMap.Count;

        // ── Custom courses ───────────────────────────────────────────────────────────────────
        // courseIdMap works in the EXTERNALLY SHIFTED id space (offset + raw id), matching every
        // consumer below (Sessions/CourseGoals/CourseResources/SessionTemplates' CourseId,
        // Settings' id lists) - built-in catalog ids (< offset) never appear here and pass
        // through unchanged wherever referenced, exactly like every other part of the app that
        // resolves a CourseId (no catalog membership check anywhere else either).
        var courseIdMap = new Dictionary<int, int>();
        var customCourseRows = new List<(int OldId, CustomCourseEntity Entity)>();
        foreach (var dto in import.CustomCourses)
        {
            if (!programIdMap.TryGetValue(dto.StudyProgramId, out var newProgramId)) { Drop("customCourses"); continue; }
            int? newGroupId = null;
            if (dto.CourseGroupId.HasValue)
            {
                if (groupIdMap.TryGetValue(dto.CourseGroupId.Value, out var mappedGroupId)) newGroupId = mappedGroupId;
                else Drop("customCourseGroupRefs"); // course still imported, just without its elective group
            }
            var entity = new CustomCourseEntity
            {
                StudyProgramId = newProgramId,
                Semester = dto.Semester,
                Name = dto.Name,
                Code = dto.Code,
                Color = dto.Color,
                Icon = dto.Icon,
                Ects = dto.Ects,
                CourseGroupId = newGroupId,
                Topics = dto.Topics,
            };
            customCourseRows.Add((dto.Id, entity));
        }
        await InsertInBatchesAsync(customCourseRows, r => r.Entity,
            r => courseIdMap[offset + r.OldId] = offset + r.Entity.Id);
        imported["customCourses"] = courseIdMap.Count;

        // Remaps a single externally-shifted-or-built-in CourseId. Built-in ids (< offset) pass
        // through unchanged and unvalidated. A custom id (>= offset) not found in courseIdMap is
        // dangling (dropped above, or simply never existed in the file) -> null.
        int? RemapCourseId(int oldCourseId) =>
            oldCourseId < offset ? oldCourseId : courseIdMap.TryGetValue(oldCourseId, out var v) ? v : null;

        // ── Session templates ────────────────────────────────────────────────────────────────
        var templateRows = new List<SessionTemplateEntity>();
        foreach (var dto in import.SessionTemplates)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("sessionTemplates"); continue; }
            templateRows.Add(new SessionTemplateEntity
            {
                Name = dto.Name,
                CourseId = newCourseId.Value,
                CourseName = dto.CourseName,
                CourseColor = dto.CourseColor,
                DurationMinutes = dto.DurationMinutes,
                Topic = dto.Topic,
                DefaultWeekday = dto.DefaultWeekday,
                DefaultStartTime = dto.DefaultStartTime,
                CreatedAt = dto.CreatedAt,
            });
        }
        await InsertInBatchesAsync(templateRows, e => e);
        imported["sessionTemplates"] = templateRows.Count;

        // ── Sessions ─────────────────────────────────────────────────────────────────────────
        // sessionIdMap (old StudySessionDto.Id -> new StudySessionEntity.Id) is needed below for
        // Note.SessionId - sessions must exist before notes can reference them.
        var sessionIdMap = new Dictionary<int, int>();
        var sessionRows = new List<(int OldId, StudySessionEntity Entity)>();
        foreach (var dto in import.Sessions)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("sessions"); continue; }
            var entity = new StudySessionEntity
            {
                CourseId = newCourseId.Value,
                CourseName = dto.CourseName,
                CourseColor = dto.CourseColor,
                StartTime = dto.StartTime,
                EndTime = dto.EndTime,
                Topic = dto.Topic,
                Notes = dto.Notes,
                IsCompleted = dto.IsCompleted,
                TimerModeId = dto.TimerModeId,
                RecurrenceGroupId = dto.RecurrenceGroupId,
            };
            sessionRows.Add((dto.Id, entity));
        }
        await InsertInBatchesAsync(sessionRows, r => r.Entity, r => sessionIdMap[r.OldId] = r.Entity.Id);
        imported["sessions"] = sessionIdMap.Count;

        // ── Notes ────────────────────────────────────────────────────────────────────────────
        // Two passes: RelatedNoteIds references OTHER notes in the same file, whose new ids are
        // only known once EVERY note has been inserted at least once. Deliberately the one pass
        // that does NOT go through InsertInBatchesAsync: the second pass below UPDATEs these
        // same rows, so they have to stay tracked until then - and they were already inserted
        // in one batched SaveChangesAsync before this change, never row by row.
        var noteIdMap = new Dictionary<int, int>();
        var noteEntities = new List<(NoteDto Dto, NoteEntity Entity)>();
        foreach (var dto in import.Notes)
        {
            int? newCourseId = null;
            if (dto.CourseId.HasValue)
            {
                newCourseId = RemapCourseId(dto.CourseId.Value);
                if (newCourseId is null) Drop("noteCourseRefs"); // note kept, just unlinked from the course
            }
            int? newSessionId = null;
            if (dto.SessionId.HasValue)
            {
                if (sessionIdMap.TryGetValue(dto.SessionId.Value, out var mappedSessionId)) newSessionId = mappedSessionId;
                else Drop("noteSessionRefs"); // note kept, just unlinked from the session
            }
            var entity = new NoteEntity
            {
                Title = dto.Title,
                Content = dto.Content,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt,
                CourseId = newCourseId,
                SessionId = newSessionId,
                IsMarkdown = dto.IsMarkdown,
                SourceUrl = dto.SourceUrl,
                Tags = dto.Tags,
                Summary = dto.Summary,
                // RelatedNoteIds filled in the second pass below, once every note has an id.
            };
            db.Notes.Add(entity);
            noteEntities.Add((dto, entity));
        }
        await db.SaveChangesAsync();
        foreach (var (dto, entity) in noteEntities) noteIdMap[dto.Id] = entity.Id;

        foreach (var (dto, entity) in noteEntities)
        {
            var remapped = new List<int>();
            foreach (var oldRelatedId in dto.RelatedNoteIds)
            {
                if (noteIdMap.TryGetValue(oldRelatedId, out var newRelatedId)) remapped.Add(newRelatedId);
                else Drop("noteRelatedIds");
            }
            entity.RelatedNoteIds = remapped.Count > 0 ? string.Join(",", remapped) : null;
        }
        await db.SaveChangesAsync();
        imported["notes"] = noteEntities.Count;

        // ── Course goals ─────────────────────────────────────────────────────────────────────
        var goalRows = new List<CourseGoalEntity>();
        foreach (var dto in import.CourseGoals)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("courseGoals"); continue; }
            goalRows.Add(new CourseGoalEntity
            {
                CourseId = newCourseId.Value,
                CourseName = dto.CourseName,
                TargetDate = dto.TargetDate,
                CompletionNote = dto.CompletionNote,
                CompletedAt = dto.CompletedAt,
                Grade = dto.Grade,
                CompletedTopics = dto.CompletedTopics,
                Tag = dto.Tag,
            });
        }
        await InsertInBatchesAsync(goalRows, e => e);
        imported["courseGoals"] = goalRows.Count;

        // ── Course resources ─────────────────────────────────────────────────────────────────
        var resourceRows = new List<CourseResourceEntity>();
        foreach (var dto in import.CourseResources)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("courseResources"); continue; }
            resourceRows.Add(new CourseResourceEntity
            {
                CourseId = newCourseId.Value,
                Title = dto.Title,
                Url = dto.Url,
                CreatedAt = dto.CreatedAt,
            });
        }
        await InsertInBatchesAsync(resourceRows, e => e);
        imported["courseResources"] = resourceRows.Count;

        // ── Settings (singleton row) ─────────────────────────────────────────────────────────
        var selectedCourseIds = new List<int>();
        foreach (var oldId in import.Settings.SelectedCourseIds)
        {
            if (RemapCourseId(oldId) is int v) selectedCourseIds.Add(v);
            else Drop("settingsSelectedCourseIds");
        }
        var completedCourseIds = new List<int>();
        foreach (var oldId in import.Settings.CompletedCourseIds)
        {
            if (RemapCourseId(oldId) is int v) completedCourseIds.Add(v);
            else Drop("settingsCompletedCourseIds");
        }
        int? newActiveStudyProgramId = null;
        if (import.Settings.ActiveStudyProgramId.HasValue)
        {
            if (programIdMap.TryGetValue(import.Settings.ActiveStudyProgramId.Value, out var mappedProgramId))
                newActiveStudyProgramId = mappedProgramId;
            else
                // Falls back to the built-in program (null), same as when an active program is
                // deleted elsewhere (StudyProgramService.DeleteAsync).
                Drop("settingsActiveStudyProgramRef");
        }
        db.Settings.Add(new UserSettingsEntity
        {
            SelectedCourseIds = string.Join(",", selectedCourseIds),
            CompletedCourseIds = string.Join(",", completedCourseIds),
            Theme = import.Settings.Theme,
            AccentColor = import.Settings.AccentColor,
            AutoSwitchFocus = import.Settings.AutoSwitchFocus,
            AutoSwitchMinutesBefore = import.Settings.AutoSwitchMinutesBefore,
            MotivationalStyle = import.Settings.MotivationalStyle,
            SessionReminderMinutes = import.Settings.SessionReminderMinutes,
            CourseGoalReminderDays = import.Settings.CourseGoalReminderDays,
            InactivityThresholdDays = import.Settings.InactivityThresholdDays,
            StudyWindowStartHour = import.Settings.StudyWindowStartHour,
            StudyWindowEndHour = import.Settings.StudyWindowEndHour,
            StudyDays = import.Settings.StudyDays,
            TargetGraduationDate = import.Settings.TargetGraduationDate,
            CustomTimerModes = import.Settings.CustomTimerModes,
            WeeklyGoalMinHours = import.Settings.WeeklyGoalMinHours,
            WeeklyGoalMaxHours = import.Settings.WeeklyGoalMaxHours,
            MonthlyGoalMinHours = import.Settings.MonthlyGoalMinHours,
            MonthlyGoalMaxHours = import.Settings.MonthlyGoalMaxHours,
            SessionRemindersEnabled = import.Settings.SessionRemindersEnabled,
            CourseGoalRemindersEnabled = import.Settings.CourseGoalRemindersEnabled,
            InactivityRemindersEnabled = import.Settings.InactivityRemindersEnabled,
            AchievementNotificationsEnabled = import.Settings.AchievementNotificationsEnabled,
            WeeklyReportEnabled = import.Settings.WeeklyReportEnabled,
            DailyMotivationEnabled = import.Settings.DailyMotivationEnabled,
            PerCourseInactivityRemindersEnabled = import.Settings.PerCourseInactivityRemindersEnabled,
            StreakRiskRemindersEnabled = import.Settings.StreakRiskRemindersEnabled,
            WeeklyGoalNudgeEnabled = import.Settings.WeeklyGoalNudgeEnabled,
            CourseAlmostDoneRemindersEnabled = import.Settings.CourseAlmostDoneRemindersEnabled,
            BestStudyTimeRemindersEnabled = import.Settings.BestStudyTimeRemindersEnabled,
            ComebackNudgeEnabled = import.Settings.ComebackNudgeEnabled,
            NewRecordNotificationsEnabled = import.Settings.NewRecordNotificationsEnabled,
            MonthlyReportEnabled = import.Settings.MonthlyReportEnabled,
            // LastBackupDownloadAt/ProgressShareEnabled/ProgressShareToken deliberately NOT
            // carried over - same "not part of the normal settings write path" rationale as
            // SettingsService.SaveAsync (see UserSettingsEntity): a fresh import shouldn't
            // silently re-activate a public progress-share link or backdate the reminder.
            ActiveStudyProgramId = newActiveStudyProgramId,
        });
        await db.SaveChangesAsync();
        imported["settings"] = 1;

        await settingsCacheVersion.BumpAsync(authUserId);
        await transaction.CommitAsync();

        return ServiceResult<BackupImportResponseDto>.Success(
            new BackupImportResponseDto { Imported = imported, Dropped = dropped });
    }
}
