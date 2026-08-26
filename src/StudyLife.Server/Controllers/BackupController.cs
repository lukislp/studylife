using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Backup &amp; export of user data. Sits under /api like every other controller and thus
/// automatically requires the default ApiAccess policy (any credential) - see
/// StudyLifeAuthorizationPolicies. Deliberately does NOT additionally carry
/// [Authorize(Policy = SessionOnly)]: IsOwnerAsync below needs to reject a merely-
/// authenticated-but-not-owner request with 403 (not 401), which the automatic SessionOnly
/// policy pipeline cannot express (it always challenges with 401, see
/// AlwaysChallengeAuthorizationMiddlewareResultHandler) - so this stays a manual check that
/// calls Forbid() itself.
/// </summary>
[ApiController]
[Route("api/backup")]
public class BackupController : ControllerBase
{
    private readonly StudyLifeDb _db;
    // Null in Postgres mode (not registered there, see Program.cs) - the raw backup/restore
    // is deliberately a single-instance/SQLite-only feature (online backup API on a single
    // local file, no equivalent for multiple pods without a guaranteed shared volume). The six
    // raw endpoints below report 501 there instead of crashing; GET /api/backup/export (JSON,
    // runs via normal EF queries) remains available unchanged across providers.
    private readonly DatabaseBackupService? _backupService;
    private readonly DatabaseRestoreService? _restoreService;
    private readonly SettingsCacheVersion _settingsCacheVersion;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOwnershipService _ownership;
    // Same JsonSerializerOptions instance the MVC pipeline itself uses for every other
    // controller's [FromBody]/ActionResult<T> (de)serialization (camelCase, case-insensitive -
    // ASP.NET Core's JsonSerializerDefaults.Web, never overridden in Program.cs). Export() uses
    // it explicitly (it returns a downloadable File(), not a plain ActionResult<T>, so it can't
    // just rely on the framework's own output formatter) - this is exactly the fix for the
    // divergent-casing bug (audit finding M4(a)): no more `new JsonSerializerOptions { ... }`
    // without a naming policy.
    private readonly System.Text.Json.JsonSerializerOptions _jsonOptions;

    public BackupController(StudyLifeDb db, SettingsCacheVersion settingsCacheVersion,
        IHostApplicationLifetime lifetime, IOwnershipService ownership,
        IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions,
        DatabaseBackupService? backupService = null, DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        _backupService = backupService;
        _restoreService = restoreService;
        _settingsCacheVersion = settingsCacheVersion;
        _lifetime = lifetime;
        _ownership = ownership;
        _jsonOptions = jsonOptions.Value.JsonSerializerOptions;
    }

    /// <summary>Only registered in SQLite mode (Program.cs) - see the field comment above.</summary>
    private bool IsRawBackupAvailable => _backupService is not null && _restoreService is not null;

    /// <summary>
    /// Only the owner of the installation (AuthUserEntity.IsOwner, see OwnershipService) may
    /// download/replace/restart the RAW database file - unlike every other /api endpoint, this
    /// one affects ALL users at once (DatabaseBackupService/-RestoreService operate on the
    /// SQLite file itself, not through the filtered EF queries; a second/third account could
    /// otherwise pull the data of EVERY other user or replace the entire DB). Additionally
    /// requires a REAL session (no per-user API key) - the key is meant for Home Assistant, not
    /// for database operations. GET /api/backup/export is deliberately left out: it runs through
    /// the normal query filters and only ever returns the calling user's own data anyway.
    /// Ownership itself is an explicit, persisted flag (audit A15/A2 fix) rather than derived
    /// from insertion order at check time - that is exactly what makes a raw restore (this very
    /// controller) deterministic: the restored DB carries its own owner along with it, instead of
    /// "whoever's row happens to have the lowest Id after the swap" silently taking over.
    /// </summary>
    private Task<bool> IsOwnerAsync()
        => HttpContext.SessionAuthUserId() is int sessionUserId ? _ownership.IsOwnerAsync(sessionUserId) : Task.FromResult(false);

    /// <summary>
    /// Returns a consistent copy of the live SQLite DB as a download. Uses the same online
    /// backup API as the weekly background dump (<see cref="DatabaseBackupService"/>) - safe
    /// despite WAL mode and the BackgroundTaskService's 30s write cycle, unlike a naive
    /// File.OpenRead of the live file. Also updates UserSettingsEntity.LastBackupDownloadAt
    /// along the way, so the dashboard (Index.razor) can remind the user when their last own
    /// offsite download was too long ago - the weekly server dump alone doesn't protect
    /// against complete device loss.
    /// </summary>
    [HttpGet("database")]
    public async Task<IActionResult> DownloadDatabase()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();

        var tempPath = _backupService!.CreateTempBackup();
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(tempPath);
            await TouchLastBackupDownloadAt();
            return File(bytes, "application/octet-stream", $"studylife-backup-{DateTime.UtcNow:yyyyMMdd}.db");
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Like <see cref="DownloadDatabase"/>, but the bytes are encrypted before sending with a
    /// password supplied by the client (<see cref="BackupEncryptionService"/>). Deliberately
    /// POST with the password in the body instead of GET with a ?password= query parameter:
    /// a self-chosen user password must not end up in the URL (browser history, server access
    /// logs) - unlike the rotating ?apiKey=, which is a long-lived, system-issued token (see
    /// the SetupBackupCard.razor comment). Because it's a POST response with a file body, the
    /// client can't fetch it via a native &lt;a download&gt; - SetupBackupCard.razor does this
    /// instead via HttpClient + a collocated JS module (blob download).
    /// </summary>
    [HttpPost("database/encrypted")]
    public async Task<IActionResult> DownloadDatabaseEncrypted([FromBody] EncryptedBackupRequest request)
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();
        if (string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "A password is required to encrypt the backup." });

        var tempPath = _backupService!.CreateTempBackup();
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(tempPath);
            var encrypted = BackupEncryptionService.Encrypt(bytes, request.Password);
            await TouchLastBackupDownloadAt();
            return File(encrypted, "application/octet-stream", $"studylife-backup-{DateTime.UtcNow:yyyyMMdd}.db.enc");
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Updates UserSettingsEntity.LastBackupDownloadAt - shared logic of
    /// <see cref="DownloadDatabase"/> and <see cref="DownloadDatabaseEncrypted"/>, so both
    /// download paths are equally hooked up to the dashboard reminder feature (Index.razor).
    /// </summary>
    private async Task TouchLastBackupDownloadAt()
    {
        var settingsEntity = await _db.Settings.GetOrCreateAsync(_db);
        settingsEntity.LastBackupDownloadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
    }

    public record EncryptedBackupRequest(string Password);

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
    /// projections as the respective controllers (NotesController/CourseGoalsController/
    /// SessionsController/SettingsController/CourseResourcesController/SessionTemplatesController),
    /// plus three export-only DTOs for the tables that have no id-carrying API DTO of their own
    /// (StudyProgramExportDto/CourseGroupExportDto/CustomCourseExportDto - see their doc comments),
    /// so the export format doesn't drift from the normal API beyond what round-tripping requires.
    /// Serialized with the SAME JsonSerializerOptions the framework itself uses for every other
    /// endpoint (_jsonOptions) - the fix for the casing bug.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var sessions = await _db.Sessions.AsNoTracking()
            .Select(s => SessionsController.ToDto(s)).ToListAsync();
        var notes = await _db.Notes.AsNoTracking()
            .OrderByDescending(n => n.UpdatedAt)
            .Select(n => NotesController.ToDto(n)).ToListAsync();
        var courseGoals = await _db.CourseGoals.AsNoTracking()
            .Select(g => CourseGoalsController.ToDto(g)).ToListAsync();
        var courseResources = await _db.CourseResources.AsNoTracking()
            .OrderBy(r => r.CreatedAt)
            .Select(r => CourseResourcesController.ToDto(r)).ToListAsync();
        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync();
        var settings = SettingsController.ToDto(settingsEntity ?? new UserSettingsEntity());
        var studyPrograms = await _db.StudyPrograms.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new StudyProgramExportDto
            {
                Id = p.Id,
                Name = p.Name,
                CreatedAt = p.CreatedAt,
                IsCompleted = p.IsCompleted,
            }).ToListAsync();
        var courseGroups = await _db.CourseGroups.AsNoTracking()
            .Select(g => new CourseGroupExportDto
            {
                Id = g.Id,
                StudyProgramId = g.StudyProgramId,
                Name = g.Name,
                EctsQuota = g.EctsQuota,
            }).ToListAsync();
        var customCourses = await _db.CustomCourses.AsNoTracking()
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
        var sessionTemplates = await _db.SessionTemplates.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => SessionTemplatesController.ToDto(t)).ToListAsync();

        var export = new BackupExportDto
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

        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export, _jsonOptions);
        return File(bytes, "application/json", $"studylife-export-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    // A JSON export is text and far smaller than the raw 512MB SQLite backup (Restore below) -
    // 64MB is generous headroom even for a very large multi-year account while still bounding
    // worst-case memory use of the [FromBody] deserialization.
    internal const long MaxImportJsonBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Imports a JSON export (this instance's own, or another StudyLife instance's - v2 or
    /// legacy v1 shape, see BackupExportDto) as a FULL REPLACE of the calling user's own data:
    /// every row this user owns in every exported table is deleted, then the file's rows are
    /// inserted with freshly assigned ids, all inside one transaction. SessionOnly, but
    /// deliberately NOT owner-gated like the six raw endpoints above: this only ever touches the
    /// caller's own rows (same reasoning as GET .../export already being open to every user) -
    /// a real passkey session is still required so a bare API key (e.g. Home Assistant's) can't
    /// wipe an account on its own.
    ///
    /// Id remapping is the reason this isn't a naive bulk insert: CustomCourseEntity rows get a
    /// NEW database id on insert, so every place that references the externally shifted id
    /// (StudyProgramCatalog.CustomCourseIdOffset + old id) - Sessions/CourseGoals/
    /// CourseResources/SessionTemplates' CourseId, Settings' SelectedCourseIds/
    /// CompletedCourseIds - must be rewritten to the NEW shifted id (RemapCourseId below).
    /// Likewise CourseGroup→StudyProgram, CustomCourse→StudyProgram/CourseGroup, Settings.
    /// ActiveStudyProgramId→StudyProgram, Note.SessionId→Session, and Note.RelatedNoteIds→Note
    /// (the last two only exist AFTER their target table's own insert pass, hence the ordering
    /// below: StudyPrograms → CourseGroups → CustomCourses → SessionTemplates → Sessions →
    /// Notes (two passes) → CourseGoals → CourseResources → Settings). A reference this file's
    /// data can't resolve (unknown custom course id, a group missing from a partial export, ...)
    /// is dropped tolerantly - either the whole referencing row for a required, non-nullable
    /// field (e.g. Session.CourseId) or just the one dangling entry for an optional field/a
    /// comma-separated list (e.g. Settings.SelectedCourseIds, same tolerance as
    /// CommaSeparatedIds) - never a hard failure of the whole import. Every drop is counted in
    /// the response instead of silently vanishing.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("import-json")]
    [RequestSizeLimit(MaxImportJsonBytes)]
    public async Task<ActionResult<BackupImportResponseDto>> ImportJson([FromBody] BackupExportDto import)
    {
        // Defense in depth alongside [RequestSizeLimit] above (which only takes full effect
        // under a real Kestrel transport, enforced while reading the body stream): an explicit
        // Content-Length check here catches the same case in-process too, with a clearer error
        // than a raw aborted connection.
        if (Request.ContentLength is { } contentLength && contentLength > MaxImportJsonBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"Import file is too large (max {MaxImportJsonBytes / (1024 * 1024)} MB)." });

        // 0 = no formatVersion property in the file at all (legacy v1 - handled below by simply
        // leaving the newer collections at their empty-list default). Anything else that isn't
        // the current version is a file this server version doesn't understand.
        if (import.FormatVersion is not (0 or 2))
            return BadRequest(new { error = $"Unsupported export formatVersion {import.FormatVersion}." });

        var imported = new Dictionary<string, int>();
        var dropped = new Dictionary<string, int>();
        void Drop(string key) => dropped[key] = dropped.GetValueOrDefault(key) + 1;

        const int offset = StudyProgramCatalog.CustomCourseIdOffset;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // ── Full replace: delete every row this user owns in every exported table ───────────
        // (the global query filters in StudyLifeDb.OnModelCreating already scope every one of
        // these DbSets to the caller, so this can never touch another user's data - no
        // IgnoreQueryFilters() anywhere here, unlike DemoSeeder's table-wide wipe). Settings is
        // a singleton-per-user row (unique index on AuthUserId): deleted and freshly re-inserted
        // below like everything else, instead of the usual GetOrCreateAsync upsert.
        await _db.Sessions.ExecuteDeleteAsync();
        await _db.Notes.ExecuteDeleteAsync();
        await _db.CourseGoals.ExecuteDeleteAsync();
        await _db.CourseResources.ExecuteDeleteAsync();
        await _db.SessionTemplates.ExecuteDeleteAsync();
        await _db.CustomCourses.ExecuteDeleteAsync();
        await _db.CourseGroups.ExecuteDeleteAsync();
        await _db.StudyPrograms.ExecuteDeleteAsync();
        await _db.Settings.ExecuteDeleteAsync();

        // ── Study programs ───────────────────────────────────────────────────────────────────
        var programIdMap = new Dictionary<int, int>();
        foreach (var dto in import.StudyPrograms)
        {
            var entity = new StudyProgramEntity { Name = dto.Name, CreatedAt = dto.CreatedAt, IsCompleted = dto.IsCompleted };
            _db.StudyPrograms.Add(entity);
            await _db.SaveChangesAsync();
            programIdMap[dto.Id] = entity.Id;
        }
        imported["studyPrograms"] = programIdMap.Count;

        // ── Elective groups ──────────────────────────────────────────────────────────────────
        var groupIdMap = new Dictionary<int, int>();
        foreach (var dto in import.CourseGroups)
        {
            if (!programIdMap.TryGetValue(dto.StudyProgramId, out var newProgramId)) { Drop("courseGroups"); continue; }
            var entity = new CourseGroupEntity { StudyProgramId = newProgramId, Name = dto.Name, EctsQuota = dto.EctsQuota };
            _db.CourseGroups.Add(entity);
            await _db.SaveChangesAsync();
            groupIdMap[dto.Id] = entity.Id;
        }
        imported["courseGroups"] = groupIdMap.Count;

        // ── Custom courses ───────────────────────────────────────────────────────────────────
        // courseIdMap works in the EXTERNALLY SHIFTED id space (offset + raw id), matching every
        // consumer below (Sessions/CourseGoals/CourseResources/SessionTemplates' CourseId,
        // Settings' id lists) - built-in catalog ids (< offset) never appear here and pass
        // through unchanged wherever referenced, exactly like every other part of the app that
        // resolves a CourseId (no catalog membership check anywhere else either).
        var courseIdMap = new Dictionary<int, int>();
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
            _db.CustomCourses.Add(entity);
            await _db.SaveChangesAsync();
            courseIdMap[offset + dto.Id] = offset + entity.Id;
        }
        imported["customCourses"] = courseIdMap.Count;

        // Remaps a single externally-shifted-or-built-in CourseId. Built-in ids (< offset) pass
        // through unchanged and unvalidated. A custom id (>= offset) not found in courseIdMap is
        // dangling (dropped above, or simply never existed in the file) -> null.
        int? RemapCourseId(int oldCourseId) =>
            oldCourseId < offset ? oldCourseId : courseIdMap.TryGetValue(oldCourseId, out var v) ? v : null;

        // ── Session templates ────────────────────────────────────────────────────────────────
        var templateCount = 0;
        foreach (var dto in import.SessionTemplates)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("sessionTemplates"); continue; }
            _db.SessionTemplates.Add(new SessionTemplateEntity
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
            templateCount++;
        }
        await _db.SaveChangesAsync();
        imported["sessionTemplates"] = templateCount;

        // ── Sessions ─────────────────────────────────────────────────────────────────────────
        // sessionIdMap (old StudySessionDto.Id -> new StudySessionEntity.Id) is needed below for
        // Note.SessionId - sessions must exist before notes can reference them.
        var sessionIdMap = new Dictionary<int, int>();
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
            _db.Sessions.Add(entity);
            await _db.SaveChangesAsync();
            sessionIdMap[dto.Id] = entity.Id;
        }
        imported["sessions"] = sessionIdMap.Count;

        // ── Notes ────────────────────────────────────────────────────────────────────────────
        // Two passes: RelatedNoteIds references OTHER notes in the same file, whose new ids are
        // only known once EVERY note has been inserted at least once.
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
            _db.Notes.Add(entity);
            noteEntities.Add((dto, entity));
        }
        await _db.SaveChangesAsync();
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
        await _db.SaveChangesAsync();
        imported["notes"] = noteEntities.Count;

        // ── Course goals ─────────────────────────────────────────────────────────────────────
        var goalCount = 0;
        foreach (var dto in import.CourseGoals)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("courseGoals"); continue; }
            _db.CourseGoals.Add(new CourseGoalEntity
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
            goalCount++;
        }
        await _db.SaveChangesAsync();
        imported["courseGoals"] = goalCount;

        // ── Course resources ─────────────────────────────────────────────────────────────────
        var resourceCount = 0;
        foreach (var dto in import.CourseResources)
        {
            var newCourseId = RemapCourseId(dto.CourseId);
            if (newCourseId is null) { Drop("courseResources"); continue; }
            _db.CourseResources.Add(new CourseResourceEntity
            {
                CourseId = newCourseId.Value,
                Title = dto.Title,
                Url = dto.Url,
                CreatedAt = dto.CreatedAt,
            });
            resourceCount++;
        }
        await _db.SaveChangesAsync();
        imported["courseResources"] = resourceCount;

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
                // deleted elsewhere (StudyProgramsController.Delete).
                Drop("settingsActiveStudyProgramRef");
        }
        _db.Settings.Add(new UserSettingsEntity
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
            // SettingsController.Save (see UserSettingsEntity): a fresh import shouldn't
            // silently re-activate a public progress-share link or backdate the reminder.
            ActiveStudyProgramId = newActiveStudyProgramId,
        });
        await _db.SaveChangesAsync();
        imported["settings"] = 1;

        _settingsCacheVersion.Value++;
        await transaction.CommitAsync();

        return Ok(new BackupImportResponseDto { Imported = imported, Dropped = dropped });
    }

    /// <summary>
    /// Accepts a .db backup previously downloaded via GET /api/backup/database (optionally
    /// POST .../encrypted) and stages it for the next restart. Deliberately NO immediate live
    /// swap (open DbContextPool + WAL, see DatabaseRestoreService) and deliberately ONLY the
    /// .db backup as a source - the JSON export intentionally omits tables and isn't suitable
    /// as a full-fidelity restore. Flow: temp file → if encrypted (magic header, see
    /// BackupEncryptionService) decrypt with <paramref name="password"/> → validation
    /// (integrity_check + core tables, otherwise 400) → automatic safety backup of the current
    /// live DB → staging. From decryption onward, exactly the same validate/stage pipeline
    /// runs as for a plaintext backup - no second, parallel restore logic.
    /// Response 202: only applied on the next restart (Program.cs →
    /// DatabaseRestoreService.ApplyPendingRestore); until then the app keeps running unchanged.
    /// </summary>
    [HttpPost("restore")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public async Task<IActionResult> Restore(IFormFile? file, [FromForm] string? password = null)
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No backup file was uploaded." });

        var tempPath = Path.Combine(Path.GetTempPath(), $"studylife-restore-upload-{Guid.NewGuid():N}.db");
        // Only populated if the upload was actually encrypted - a separate file instead of
        // in-place decryption, so that tempPath (the original upload bytes) remains untouched
        // for diagnosis/retry in case of an error.
        string? decryptedTempPath = null;
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }

            var workingPath = tempPath;
            if (BackupEncryptionService.IsEncryptedFile(tempPath))
            {
                if (string.IsNullOrEmpty(password))
                    return BadRequest(new { error = "This backup is encrypted. Please provide the password.", encrypted = true });

                try
                {
                    var encryptedBytes = await System.IO.File.ReadAllBytesAsync(tempPath);
                    var plaintext = BackupEncryptionService.Decrypt(encryptedBytes, password);
                    decryptedTempPath = tempPath + ".decrypted";
                    await System.IO.File.WriteAllBytesAsync(decryptedTempPath, plaintext);
                    workingPath = decryptedTempPath;
                }
                catch (BackupDecryptionException ex)
                {
                    // A wrong password and a tampered/corrupted encrypted file are
                    // indistinguishable at this point (the AES-GCM auth tag covers both) -
                    // encrypted:true still lets the client specifically show "ask for the
                    // password again" instead of the generic upload error message.
                    return BadRequest(new { error = ex.Message, encrypted = true });
                }
            }

            var validationError = DatabaseRestoreService.Validate(workingPath);
            if (validationError != null)
                return BadRequest(new { error = validationError });

            // Safety net BEFORE staging, unprompted: a consistent copy of the current
            // live DB into app_data/backups/prerestore-*.db (same online backup code path
            // as download/weekly dump).
            var safetyBackupPath = _backupService!.CreatePreRestoreBackup();

            _restoreService!.Stage(workingPath);

            return Accepted(new
            {
                status = "staged",
                message = "Backup verified and staged. Restart the StudyLife server to apply it "
                          + "(e.g. \"docker restart studylife-server\"). Until then the app keeps "
                          + "running on the current data.",
                safetyBackup = Path.GetFileName(safetyBackupPath),
            });
        }
        finally
        {
            // Also clean up -wal/-shm: the read-only validation can create sidecars next to
            // the temp file if the upload file is marked as being in WAL mode.
            var cleanupPaths = decryptedTempPath == null
                ? new[] { tempPath, tempPath + "-wal", tempPath + "-shm" }
                : new[] { tempPath, tempPath + "-wal", tempPath + "-shm",
                          decryptedTempPath, decryptedTempPath + "-wal", decryptedTempPath + "-shm" };
            foreach (var path in cleanupPaths)
            {
                try { System.IO.File.Delete(path); } catch (IOException) { /* best effort */ }
            }
        }
    }

    /// <summary>Is a staged restore currently ready for the next restart?</summary>
    [HttpGet("restore/status")]
    public async Task<ActionResult<RestoreStatusResponseDto>> RestoreStatus()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();
        return Ok(new RestoreStatusResponseDto { Pending = _restoreService!.IsRestorePending, StagedAt = _restoreService.StagedAtUtc });
    }

    /// <summary>Discards a staged restore before it was applied. Live DB untouched.</summary>
    [HttpPost("restore/cancel")]
    public async Task<ActionResult<RestoreCancelResponseDto>> CancelRestore()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();
        return _restoreService!.CancelPending()
            ? Ok(new RestoreCancelResponseDto())
            : NotFound(new { error = "No staged restore to cancel." });
    }

    /// <summary>
    /// Convenience restart, ONLY allowed while a restore is staged (409 otherwise - not a
    /// generic remote-kill endpoint). Shuts down the host cleanly after a short delay; in
    /// the default deployment (docker-compose.yml: restart: unless-stopped), Docker
    /// automatically restarts the container afterward and Program.cs applies the restore.
    /// If the app is NOT running under an auto-restart policy (e.g. locally via dotnet run),
    /// it stays stopped - which is why the API response and client UI always also document
    /// the manual restart as a reliable fallback.
    /// </summary>
    [HttpPost("restore/restart")]
    public async Task<IActionResult> RestartToApply()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401: Forbid() now works fine (a real AuthenticationHandler is registered,
        // see StudyLifeAuthenticationHandler.HandleForbiddenAsync - it writes exactly the same
        // bare 403 status, no body, that StatusCode(403) used to), but plain 401 would still be
        // wrong here - the client (SessionHandler.cs) interprets 401 as "session dead, log out",
        // and this is a valid, merely unprivileged session (the user is genuinely logged in,
        // just not the owner), which must NOT log the client out (observed live as a bug: a
        // second user got kicked out when opening the setup page, because SetupRestoreCard
        // fetches restore/status). That's also why this stays a manual owner check instead of
        // [Authorize(Policy = SessionOnly)]: that policy always challenges with 401 (see
        // AlwaysChallengeAuthorizationMiddlewareResultHandler), which is exactly the response
        // this endpoint must NOT give for "logged in, but not the owner".
        if (!await IsOwnerAsync()) return Forbid();
        if (!_restoreService!.IsRestorePending)
            return Conflict(new { error = "No staged restore - refusing to restart the server." });

        var lifetime = _lifetime;
        _ = Task.Run(async () =>
        {
            // Short delay so the 202 response still reliably reaches the client.
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            lifetime.StopApplication();
        });

        return Accepted(new
        {
            status = "restarting",
            message = "Server is shutting down to apply the restore. If it is not managed by an "
                      + "auto-restart policy (Docker restart: unless-stopped), start it manually.",
        });
    }
}
