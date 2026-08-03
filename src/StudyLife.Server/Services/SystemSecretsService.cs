using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

public record VapidKeys(string Subject, string PublicKey, string PrivateKey);

/// <summary>
/// Mutable singleton wrapper for <see cref="VapidKeys"/>: the values now come from the DB
/// (SystemSecretsService), so they're only known AFTER builder.Build()+Migrate(), but as a
/// service must already be registered BEFORE Build(). Program.cs populates Keys once in the
/// startup scope (before app.Run()); all consumers (PushController, SessionsController,
/// BackgroundTaskService) are guaranteed to be constructed only afterward (both requests and
/// hosted-service start happen only through app.Run()), so Keys is always set by the time they
/// are constructed.
/// </summary>
public sealed class VapidKeysHolder
{
    public VapidKeys? Keys { get; set; }
}

/// <summary>
/// Instance-wide secrets (VAPID key pair for web push, setup code for initial registration)
/// - DB-backed instead of the former file-based storage in app_data/ (VapidKeyProvider/
/// SetupSecretService, before the scalability branch). Reason: with multiple pods without a
/// guaranteed shared volume (Kubernetes, multiple VPS instances), each pod would otherwise
/// generate its own, diverging values on a lazy-generate-on-first-access basis - push
/// subscriptions registered against pod A's public key would be signed by pod B with the wrong
/// private key. A single row in the (already shared) database fixes this identically across
/// providers, with no additional volume sharing needed - this also simplifies single-instance
/// operation (SQLite/Pi), which previously likewise relied on file I/O + an in-process lock
/// (see the now-removed VapidKeyProvider.cs).
/// </summary>
public sealed class SystemSecretsService
{
    // Used to be "mailto:studylife@localhost" - Apple's web push service hard-rejects a VAPID
    // JWT sub claim with "localhost" as the domain with 403 "BadJwtToken".
    private const string DefaultVapidSubject = "mailto:push@studylife.app";
    private static readonly string[] RejectedLegacyVapidSubjects = ["mailto:studylife@localhost"];

    private const string SetupSecretAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int SetupSecretLength = 8;

    // Fixed id instead of random AUTOINCREMENT: makes "exactly one row" explicit and lets a
    // race between multiple pods starting up simultaneously be resolved cleanly via a simple
    // PK conflict (see GetOrCreateRowAsync).
    private const int RowId = 1;

    private readonly StudyLifeDb _db;

    public SystemSecretsService(StudyLifeDb db) => _db = db;

    public async Task<VapidKeys> EnsureVapidKeysAsync(IConfiguration config)
    {
        // Config override first (e.g. ENV Vapid__PublicKey/Vapid__PrivateKey) - allows an
        // operator to pin an externally managed key pair without touching the DB.
        var configPublic = config["Vapid:PublicKey"];
        var configPrivate = config["Vapid:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(configPublic) && !string.IsNullOrWhiteSpace(configPrivate))
        {
            var configSubject = config["Vapid:Subject"];
            return new VapidKeys(
                string.IsNullOrWhiteSpace(configSubject) ? DefaultVapidSubject : configSubject,
                configPublic, configPrivate);
        }

        await GetOrCreateRowAsync();
        var existing = await ReadVapidAsync();
        if (!string.IsNullOrWhiteSpace(existing.PublicKey) && !string.IsNullOrWhiteSpace(existing.PrivateKey))
        {
            var subject = existing.Subject;
            if (string.IsNullOrWhiteSpace(subject) || RejectedLegacyVapidSubjects.Contains(subject))
            {
                subject = DefaultVapidSubject;
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"SystemSecrets\" SET \"VapidSubject\" = {subject} WHERE \"Id\" = {RowId}");
            }
            return new VapidKeys(subject, existing.PublicKey, existing.PrivateKey);
        }

        // Multiple processes (e.g. web + worker container) start virtually simultaneously and
        // would otherwise EACH generate their own random key pair - without this "only set if
        // still empty" update, whichever process finishes last would silently overwrite the
        // others' DB state, while the others keep holding their own (now inconsistent) key pair
        // in memory. Push subscriptions registered against pod A's public key would then be
        // signed by the worker with the WRONG private key (if the worker "won") - exactly the
        // bug the DB-backed design was supposed to fix. Reproduced live in the
        // docker-compose.scale.yml test setup: the server and worker containers showed
        // different codes/keys after startup, until this atomic update path fixed it.
        var generated = WebPush.VapidHelper.GenerateVapidKeys();
        var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SystemSecrets\" SET \"VapidPublicKey\" = {generated.PublicKey}, \"VapidPrivateKey\" = {generated.PrivateKey}, \"VapidSubject\" = {DefaultVapidSubject} WHERE \"Id\" = {RowId} AND \"VapidPublicKey\" IS NULL");
        if (rowsAffected == 0)
        {
            // Another process was faster - its already-persisted key pair applies, ours,
            // just generated, is discarded (never used/exposed anywhere).
            var winner = await ReadVapidAsync();
            return new VapidKeys(winner.Subject!, winner.PublicKey!, winner.PrivateKey!);
        }
        return new VapidKeys(DefaultVapidSubject, generated.PublicKey, generated.PrivateKey);
    }

    private async Task<(string? PublicKey, string? PrivateKey, string? Subject)> ReadVapidAsync()
    {
        var row = await _db.SystemSecrets.AsNoTracking()
            .Where(x => x.Id == RowId)
            .Select(x => new { x.VapidPublicKey, x.VapidPrivateKey, x.VapidSubject })
            .FirstAsync();
        return (row.VapidPublicKey, row.VapidPrivateKey, row.VapidSubject);
    }

    /// <summary>Reads the existing setup code or generates + persists a new one. Only call
    /// when no passkey exists yet (caller checks this beforehand).</summary>
    public async Task<string> EnsureSetupSecretAsync()
    {
        await GetOrCreateRowAsync();
        var existing = await _db.SystemSecrets.AsNoTracking()
            .Where(x => x.Id == RowId)
            .Select(x => x.SetupSecretCode)
            .FirstAsync();
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        // Same race as with the VAPID keys above: "only set if still empty", otherwise two
        // simultaneously starting processes (web + worker) could generate different codes
        // and overwrite each other - an operator would then see two different codes depending
        // on which container's logs they check, of which only one is actually valid.
        var code = GenerateSetupSecretCode();
        var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SystemSecrets\" SET \"SetupSecretCode\" = {code} WHERE \"Id\" = {RowId} AND \"SetupSecretCode\" IS NULL");
        if (rowsAffected == 0)
        {
            return await _db.SystemSecrets.AsNoTracking()
                .Where(x => x.Id == RowId)
                .Select(x => x.SetupSecretCode)
                .FirstAsync() ?? code;
        }
        return code;
    }

    /// <summary>After successful initial registration: the code is never needed again.
    /// Raw SQL UPDATE instead of a tracked-entity save: a SaveChangesAsync call here could
    /// overwrite with a stale copy from the change tracker - one loaded in an earlier
    /// GetOrCreateRowAsync call of THIS request and since superseded by a concurrent raw SQL
    /// write (see EnsureSetupSecretAsync) - (observed live as a test failure, see the
    /// ValidateSetupSecretAsync comment below).</summary>
    public Task ClearSetupSecretAsync() =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SystemSecrets\" SET \"SetupSecretCode\" = NULL WHERE \"Id\" = {RowId}");

    public async Task<bool> ValidateSetupSecretAsync(string? provided)
    {
        // AsNoTracking + a separate query instead of GetOrCreateRowAsync()'s tracked entity: EF
        // Core's identity map would otherwise return the OLD in-memory copy for a row already
        // loaded in the same DbContext, even if EnsureSetupSecretAsync has meanwhile set the
        // code via raw SQL (deliberately bypassing the change tracker, see there) - this is
        // exactly what caused a real failure reproduced in a test run (code rejected as
        // "invalid" right after being generated).
        var expected = await _db.SystemSecrets.AsNoTracking()
            .Where(x => x.Id == RowId)
            .Select(x => x.SetupSecretCode)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var expectedBytes = Encoding.UTF8.GetBytes(NormalizeSetupSecretCode(expected));
        var providedBytes = Encoding.UTF8.GetBytes(NormalizeSetupSecretCode(provided ?? ""));
        return expectedBytes.Length > 0
            && expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private async Task<SystemSecretsEntity> GetOrCreateRowAsync()
    {
        var row = await _db.SystemSecrets.FirstOrDefaultAsync(x => x.Id == RowId);
        if (row is not null) return row;

        row = new SystemSecretsEntity { Id = RowId };
        _db.SystemSecrets.Add(row);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another pod/process was faster on the very first start (unique conflict on
            // the fixed RowId) - simply re-read instead of risking a second failed attempt.
            _db.Entry(row).State = EntityState.Detached;
            row = await _db.SystemSecrets.FirstOrDefaultAsync(x => x.Id == RowId)
                ?? throw new InvalidOperationException("Could not read or create the SystemSecrets row.");
        }
        return row;
    }

    private static string NormalizeSetupSecretCode(string code) =>
        new(code.ToUpperInvariant().Where(SetupSecretAlphabet.Contains).ToArray());

    private static string GenerateSetupSecretCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(SetupSecretLength);
        var chars = new char[SetupSecretLength];
        for (var i = 0; i < SetupSecretLength; i++)
            chars[i] = SetupSecretAlphabet[bytes[i] % SetupSecretAlphabet.Length];
        var raw = new string(chars);
        return $"{raw[..4]}-{raw[4..]}";
    }
}
