using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Backup &amp; export of user data. Sits under /api like every other controller and thus
/// automatically inherits the optional Security:ApiKey protection from Program.cs (the
/// middleware applies path-based to everything under /api, not controller-specific).
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
    private readonly ICurrentUserAccessor _currentUser;

    public BackupController(StudyLifeDb db, SettingsCacheVersion settingsCacheVersion,
        IHostApplicationLifetime lifetime, ICurrentUserAccessor currentUser,
        DatabaseBackupService? backupService = null, DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        _backupService = backupService;
        _restoreService = restoreService;
        _settingsCacheVersion = settingsCacheVersion;
        _lifetime = lifetime;
        _currentUser = currentUser;
    }

    /// <summary>Only registered in SQLite mode (Program.cs) - see the field comment above.</summary>
    private bool IsRawBackupAvailable => _backupService is not null && _restoreService is not null;

    /// <summary>
    /// Only the first registered user (owner of the installation) may download/replace/
    /// restart the RAW database file - unlike every other /api endpoint, this one affects
    /// ALL users at once (DatabaseBackupService/-RestoreService operate on the SQLite file
    /// itself, not through the filtered EF queries; a second/third account could otherwise
    /// pull the data of EVERY other user or replace the entire DB). Additionally requires a
    /// REAL session (no per-user API key) - the key is meant for Home Assistant, not for
    /// database operations. GET /api/backup/export is deliberately left out: it runs through
    /// the normal query filters and only ever returns the calling user's own data anyway.
    /// </summary>
    private async Task<bool> IsOwnerAsync()
    {
        if (!HttpContext.Items.ContainsKey(AuthSessionService.SessionItemKey)) return false;
        if (_currentUser.AuthUserId is not > 0) return false;
        var firstUserId = await _db.AuthUsers.OrderBy(u => u.Id).Select(u => u.Id).FirstOrDefaultAsync();
        return _currentUser.AuthUserId == firstUserId;
    }

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

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);

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

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);
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
    /// Human-readable JSON export of the actual user data. Deliberately excluded:
    /// PushSubscriptions (this browser's endpoint registrations, not transferable user data),
    /// SentReminders (internal dedup bookkeeping), and TimerState (transient live state of the
    /// focus timer). Uses the same ToDto projections as the respective controllers
    /// (NotesController/CourseGoalsController/SessionsController/SettingsController/
    /// CourseResourcesController), so the export format doesn't drift from the normal API.
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

        var export = new
        {
            exportedAt = DateTime.UtcNow,
            sessions,
            notes,
            courseGoals,
            courseResources,
            settings,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(export,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"studylife-export-{DateTime.UtcNow:yyyyMMdd}.json");
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

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);
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
    public async Task<IActionResult> RestoreStatus()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);
        return Ok(new { pending = _restoreService!.IsRestorePending, stagedAt = _restoreService.StagedAtUtc });
    }

    /// <summary>Discards a staged restore before it was applied. Live DB untouched.</summary>
    [HttpPost("restore/cancel")]
    public async Task<IActionResult> CancelRestore()
    {
        // Postgres mode: raw backup/restore not available (see IsRawBackupAvailable comment) -
        // before the owner check, because this can never work structurally here, regardless of
        // WHO is asking.
        if (!IsRawBackupAvailable)
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Raw database backup/restore is not available in Postgres mode. Use the JSON export (GET /api/backup/export).",
            });

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);
        return _restoreService!.CancelPending()
            ? Ok(new { status = "cancelled" })
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

        // 403, NOT 401/Forbid(): Forbid() crashes in this app with an InvalidOperationException
        // (no ASP.NET Core authentication scheme registered - auth runs entirely through
        // custom middleware). 401, in turn, is interpreted by the client (SessionHandler.cs) as
        // "session dead, log out" - but this is a valid, merely unprivileged session (the user
        // is genuinely logged in, just not the owner) and must NOT log the client out (observed
        // live as a bug: a second user got kicked out when opening the setup page, because
        // SetupRestoreCard fetches restore/status). StatusCode(403) sets the code directly,
        // without the IAuthenticationService machinery of Forbid().
        if (!await IsOwnerAsync()) return StatusCode(StatusCodes.Status403Forbidden);
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
