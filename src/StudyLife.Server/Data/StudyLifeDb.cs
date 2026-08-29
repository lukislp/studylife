using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StudyLife.Server.Services;

namespace StudyLife.Server.Data;

public class StudyLifeDb : DbContext
{
    // Multi-tenant foundation (phase 1): every user-related table carries an AuthUserId,
    // and the global query filters below make EVERY existing LINQ query automatically
    // user-specific - deliberately centralized here instead of per controller, so that
    // code that may not be touched (PlannerController) stays correctly filtered too.
    // EF re-parameterizes the field access _currentUser.AuthUserId on every query execution,
    // so the filter always reads the user of the current request/background scope.
    private readonly ICurrentUserAccessor _currentUser;

    // Non-generic DbContextOptions constructor instead of DbContextOptions<StudyLifeDb>: the
    // two provider subclasses below (StudyLifeDbSqlite/StudyLifeDbPostgres, see the
    // scalability branch) each need DbContextOptions<THEIR OWN class>, so EF
    // Core can maintain a separate migration history per provider - both simply pass their own
    // options through to this constructor (implicit conversion to the base class).
    public StudyLifeDb(DbContextOptions options, ICurrentUserAccessor currentUser)
        : base(options)
        => _currentUser = currentUser;

    public DbSet<AuthUserEntity> AuthUsers => Set<AuthUserEntity>();
    public DbSet<PasskeyCredentialEntity> PasskeyCredentials => Set<PasskeyCredentialEntity>();
    public DbSet<AuthSessionEntity> AuthSessions => Set<AuthSessionEntity>();
    public DbSet<RecoveryCodeEntity> RecoveryCodes => Set<RecoveryCodeEntity>();
    public DbSet<AuthInviteEntity> AuthInvites => Set<AuthInviteEntity>();
    public DbSet<SystemSecretsEntity> SystemSecrets => Set<SystemSecretsEntity>();
    public DbSet<StudySessionEntity> Sessions => Set<StudySessionEntity>();
    public DbSet<UserSettingsEntity> Settings => Set<UserSettingsEntity>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();
    public DbSet<SentReminderEntity> SentReminders => Set<SentReminderEntity>();
    public DbSet<AiKeyOutboxEntity> AiKeyOutbox => Set<AiKeyOutboxEntity>();
    public DbSet<NoteEntity> Notes => Set<NoteEntity>();
    public DbSet<CourseGoalEntity> CourseGoals => Set<CourseGoalEntity>();
    public DbSet<TimerStateEntity> TimerState => Set<TimerStateEntity>();
    public DbSet<StudyProgramEntity> StudyPrograms => Set<StudyProgramEntity>();
    public DbSet<CourseGroupEntity> CourseGroups => Set<CourseGroupEntity>();
    public DbSet<CustomCourseEntity> CustomCourses => Set<CustomCourseEntity>();
    public DbSet<SessionTemplateEntity> SessionTemplates => Set<SessionTemplateEntity>();
    public DbSet<CourseResourceEntity> CourseResources => Set<CourseResourceEntity>();

    // SQLite stores DateTime as plain ISO-8601 text and ignores DateTimeKind entirely -
    // Npgsql, on the other hand, is strict (throws on write/read if Kind doesn't match the
    // column). Every DateTime property in this app is meant as "floating"/without timezone
    // reference anyway (see ARCHITECTURE.md on timezone handling) - this global converter
    // normalizes Kind to Unspecified on EVERY DateTime, uniformly across providers,
    // without having to touch the existing (known, accepted) Now/UtcNow mix in the caller
    // code.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UnspecifiedKindConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUnspecifiedKindConverter>();
    }

    private sealed class UnspecifiedKindConverter : ValueConverter<DateTime, DateTime>
    {
        public UnspecifiedKindConverter() : base(
            v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified),
            v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified))
        {
        }
    }

    private sealed class NullableUnspecifiedKindConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUnspecifiedKindConverter() : base(
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Unspecified) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Unspecified) : v)
        {
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Unique constraints that used to be global are now unique per user -
        // otherwise, in phase 2, a second user couldn't e.g. create a goal for the same
        // CourseId (or their "weeklyreport:<week>" SentReminder insert would collide).
        modelBuilder.Entity<CourseGoalEntity>().HasIndex(g => new { g.AuthUserId, g.CourseId }).IsUnique();
        modelBuilder.Entity<StudySessionEntity>().HasIndex(s => s.StartTime);
        modelBuilder.Entity<SentReminderEntity>().HasIndex(r => new { r.AuthUserId, r.Key }).IsUnique();
        // Security/multi-user fix: PushSubscriptionEntity.Endpoint was originally GLOBALLY unique
        // ("one push endpoint belongs to exactly one browser profile") - that only held as long as a
        // browser profile also had only one user. Once two people log in with their own accounts
        // in the same browser and both enable push, the push API returns the same
        // subscription/the same endpoint for both (it belongs to the origin, not
        // to the logged-in user) - the second INSERT attempt then violated the global
        // uniqueness (SQLite error 19). Now unique per user like CourseGoals/SentReminders:
        // both accounts get their own subscription row for the same
        // physical endpoint, each only gets ITS OWN reminders on a push event.
        modelBuilder.Entity<PushSubscriptionEntity>().HasIndex(s => new { s.AuthUserId, s.Endpoint }).IsUnique();
        modelBuilder.Entity<CourseGroupEntity>().HasIndex(g => g.StudyProgramId);
        modelBuilder.Entity<CustomCourseEntity>().HasIndex(c => c.StudyProgramId);
        modelBuilder.Entity<CourseResourceEntity>().HasIndex(r => r.CourseId);

        // "One row per user" enforced at the DB level (bug fix): UserSettingsEntity/TimerStateEntity
        // were previously singleton-per-user by convention only - multiple independent
        // get-or-create call sites (SettingsController.Save, BackupController.
        // TouchLastBackupDownloadAt, TimerStateController.Save/SetLiveActivityPushToken) could
        // race on a user's very first write and each insert their own row, after which
        // FirstOrDefaultAsync picked one of the duplicates nondeterministically forever. The
        // migration adding these indexes (AddPerUserUniqueRows/-Postgres) deduplicates any
        // already-existing poisoned rows first; EntityUpsertHelper.GetOrCreateAsync now relies on
        // this index as the actual race lock (same claim-first pattern as SentReminders).
        modelBuilder.Entity<UserSettingsEntity>().HasIndex(s => s.AuthUserId).IsUnique();
        modelBuilder.Entity<TimerStateEntity>().HasIndex(s => s.AuthUserId).IsUnique();

        // Passkey auth (phase 2): CredentialId identifies a passkey uniquely worldwide
        // (generated by the authenticator), TokenHash identifies a session - both are primary lookup
        // paths of the login/gate middleware and are therefore uniquely indexed.
        modelBuilder.Entity<PasskeyCredentialEntity>().HasIndex(c => c.CredentialId).IsUnique();
        modelBuilder.Entity<AuthSessionEntity>().HasIndex(s => s.TokenHash).IsUnique();
        // Per-user API key (phase 3): the gate resolves the user via the hash of the submitted
        // X-Api-Key - same primary lookup path as AuthSessions.TokenHash, hence
        // also uniquely indexed (multiple NULLs are allowed in SQLite unique indexes).
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.ApiKeyHash).IsUnique();
        // Per-user API key for studylife-ai: separate slot from ApiKeyHash (see
        // AuthUserEntity.AiApiKeyHash), same uniqueness reasoning.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.AiApiKeyHash).IsUnique();
        // Per-user API key for studylife-mcp: separate slot from ApiKeyHash/AiApiKeyHash (see
        // AuthUserEntity.McpApiKeyHash), same uniqueness reasoning.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.McpApiKeyHash).IsUnique();
        // Per-user API key for the studylife-capture browser extension: separate slot from the
        // three above (see AuthUserEntity.CaptureApiKeyHash), same uniqueness reasoning.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.CaptureApiKeyHash).IsUnique();
        // Per-user API key for the studylife-focusguard browser extension: separate slot from the
        // four above (see AuthUserEntity.FocusGuardApiKeyHash), same uniqueness reasoning.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.FocusGuardApiKeyHash).IsUnique();
        // Per-user API key for the studylife-focustunes browser extension: separate slot from the
        // five above (see AuthUserEntity.FocusTunesApiKeyHash), same uniqueness reasoning.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.FocusTunesApiKeyHash).IsUnique();
        // AI key outbox (audit A7): drained table-wide by BackgroundTaskService across all
        // users in one query (see RunAiKeyOutboxAsync), not per-user - the index supports the
        // per-user CreatedAt ordering that drain does in memory.
        modelBuilder.Entity<AiKeyOutboxEntity>().HasIndex(o => o.AuthUserId);

        // Calendar feed (security fix): resolves the user analogous to the API key, instead of
        // (as before, global CalendarTokenProvider) giving every caller the same,
        // user-independent token - see AuthUserEntity.CalendarToken.
        modelBuilder.Entity<AuthUserEntity>().HasIndex(u => u.CalendarToken).IsUnique();

        // Global query filters: one line per user-related table. 0 (= no user
        // resolved) never matches, because real AuthUserIds start at 1 and the migration
        // AddMultiTenantAuthUserFoundation backfills all existing rows.
        modelBuilder.Entity<StudySessionEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<UserSettingsEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<PushSubscriptionEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<SentReminderEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<NoteEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<CourseGoalEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<TimerStateEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<StudyProgramEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<CourseGroupEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<CustomCourseEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<SessionTemplateEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);
        modelBuilder.Entity<CourseResourceEntity>().HasQueryFilter(e => e.AuthUserId == _currentUser.AuthUserId);

        // PasskeyCredentialEntity, AuthSessionEntity, and RecoveryCodeEntity deliberately get
        // NO query filter: login/session validation necessarily run BEFORE a user
        // is resolved (the middleware searches the session table-wide via TokenHash,
        // login/begin collects the CredentialIds of ALL users, recovery/login searches the
        // code hash table-wide) - a filter would make exactly these paths return nothing.
        // User-specific accesses (device list, recovery/generate) filter explicitly in the
        // controller.
        modelBuilder.Entity<RecoveryCodeEntity>().HasIndex(e => e.CodeHash).IsUnique();
        // Registration invites (audit A10): TryConsumeInviteAsync's atomic
        // "UPDATE ... WHERE TokenHash = @hash AND UsedAt IS NULL" (see RegistrationGateService)
        // relies on this being unique - two concurrent register/complete calls racing on the same
        // token can then never both succeed. No query filter (same reasoning as
        // RecoveryCodeEntity/AuthSessionEntity above) - invite validation/consumption resolves
        // the row directly by TokenHash, before any AuthUserId is known.
        modelBuilder.Entity<AuthInviteEntity>().HasIndex(i => i.TokenHash).IsUnique();
        // SystemSecretsEntity likewise without a filter (and without AuthUserId) - it is not a
        // user-data table but instance-wide configuration (VAPID keys, setup code,
        // see SystemSecretsService), exactly one row for the entire installation.

        // Postgres-specific: Npgsql maps DateTime by default to "timestamp with time
        // zone" (STRICTLY requires Kind=Utc) - but the UnspecifiedKindConverter above deliberately
        // normalizes to Kind=Unspecified (all DateTime properties of this app are "floating"
        // local time without a zone reference, see ARCHITECTURE.md), which crashes at runtime with
        // "Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time
        // zone'" (encountered live in the docker-compose.scale.yml test setup on the first
        // registration). Fix: explicitly force every DateTime/DateTime? column in Postgres mode
        // to "timestamp without time zone", so the Npgsql type and converter semantics
        // match up. SQLite doesn't know this distinction (plain ISO-8601 text) -
        // hence only in the Postgres branch, to avoid touching the already-migrated SQLite
        // history.
        if (Database.IsNpgsql())
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                        property.SetColumnType("timestamp without time zone");
                }
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuthUserIdOnAddedEntries();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuthUserIdOnAddedEntries();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Automatically sets AuthUserId on every newly inserted row that doesn't yet carry a
    /// user. Centralized in the DbContext instead of at every individual .Add call site, because
    /// at least one insert site (PlannerController) may not be touched - without this safety net,
    /// rows created there would end up with AuthUserId 0 and would be invisible through the query
    /// filters.
    /// </summary>
    private void StampAuthUserIdOnAddedEntries()
    {
        int? userId = null;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;
            var property = entry.Metadata.FindProperty(nameof(StudySessionEntity.AuthUserId));
            if (property == null || property.ClrType != typeof(int)) continue;
            if (entry.Property(property.Name).CurrentValue is int current && current != 0) continue;
            userId ??= _currentUser.AuthUserId;
            if (userId == 0) continue; // no user in context - leave the value untouched
            entry.Property(property.Name).CurrentValue = userId;
        }
    }
}

/// <summary>
/// Provider subclass ONLY for the Postgres path of the scalability variant (Database:Provider
/// configuration, see Program.cs) - a pure marker type without its own code. EF Core needs a
/// separate migration history per provider (SQLite and Postgres SQL are not interchangeable),
/// and migration assignment runs via the concrete DbContext type (every migration carries
/// `[DbContext(typeof(X))]`), not via a runtime property. For SQLite there is DELIBERATELY NO
/// own subclass: all 38 existing migrations are tagged to the base class
/// <see cref="StudyLifeDb"/> (created long before this branch) - a
/// "StudyLifeDbSqlite" subclass would have made these migrations invisible to EF Core (empty
/// migration history, not a single table would be created). The SQLite path therefore continues to
/// register <see cref="StudyLifeDb"/> itself directly (see Program.cs) - exactly as before this
/// branch. Controllers/services inject exclusively the base class
/// <see cref="StudyLifeDb"/> either way and don't notice which provider is active.
/// </summary>
public sealed class StudyLifeDbPostgres : StudyLifeDb
{
    public StudyLifeDbPostgres(DbContextOptions<StudyLifeDbPostgres> options, ICurrentUserAccessor currentUser)
        : base(options, currentUser)
    {
    }
}

/// <summary>
/// An account/user of the app (phase 1 of the multi-user rework). In phase 1 exactly
/// one row exists (created by the migration AddMultiTenantAuthUserFoundation, gets all
/// existing data assigned to it); actual registration of new users only comes with the
/// passkey login in phase 2. All user-related tables reference this Id via
/// their AuthUserId field - deliberately without an FK constraint, the same loose pattern as CourseId.
/// </summary>
public class AuthUserEntity
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash (lowercase hex, same format as AuthSessionEntity.TokenHash) of the per-user
    /// API key for Home Assistant and similar non-interactive integrations (phase 3).
    /// Deliberately LONG-LIVED (explicit user decision): no automatic rotation, no
    /// expiration date - HA has no live session that could notice a rotation; a
    /// silently rotating key would quietly disable the integration after 30-40 days.
    /// The plaintext leaves the server exactly once (response of ha-api-key/generate).
    /// Null = no key generated, or revoked.
    /// </summary>
    public string? ApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation - only for the "key active since ..." display
    /// in the setup UI, has no expiration semantics whatsoever.</summary>
    public DateTime? ApiKeyCreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash of the per-user API key for the studylife-ai integration - same shape and
    /// same reasoning as <see cref="ApiKeyHash"/> (long-lived, no rotation), but a SEPARATE
    /// credential/slot: generating or revoking this one must not affect Home Assistant's key
    /// and vice versa (independent blast radius if one integration's key ever leaks).
    /// Null = no key generated, or revoked.
    /// </summary>
    public string? AiApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation of <see cref="AiApiKeyHash"/> - display-only, same as ApiKeyCreatedAt.</summary>
    public DateTime? AiApiKeyCreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash of the per-user API key for the studylife-mcp integration (an MCP server
    /// exposing StudyLife data to Claude Desktop and other MCP clients) - same shape and same
    /// reasoning as <see cref="ApiKeyHash"/> (long-lived, no rotation), but a SEPARATE
    /// credential/slot: generating or revoking this one must not affect Home Assistant's or
    /// studylife-ai's key and vice versa (independent blast radius if one integration's key
    /// ever leaks). Null = no key generated, or revoked.
    /// </summary>
    public string? McpApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation of <see cref="McpApiKeyHash"/> - display-only, same as ApiKeyCreatedAt.</summary>
    public DateTime? McpApiKeyCreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash of the per-user API key for the studylife-capture browser extension - same
    /// shape and same reasoning as <see cref="ApiKeyHash"/> (long-lived, no rotation), but a
    /// SEPARATE credential/slot: generating or revoking this one must not affect Home
    /// Assistant's, studylife-ai's, or studylife-mcp's key and vice versa (independent blast
    /// radius if one integration's key ever leaks - a browser extension key in particular is
    /// stored in extension settings on a device that isn't this server, a materially different
    /// exposure than the others). Null = no key generated, or revoked.
    /// </summary>
    public string? CaptureApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation of <see cref="CaptureApiKeyHash"/> - display-only, same as ApiKeyCreatedAt.</summary>
    public DateTime? CaptureApiKeyCreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash of the per-user API key for the studylife-focusguard browser extension - same
    /// shape and same reasoning as <see cref="CaptureApiKeyHash"/> (long-lived, no rotation, key
    /// lives on a device that isn't this server), but a SEPARATE credential/slot: generating or
    /// revoking this one must not affect any of the other four and vice versa. By far the
    /// narrowest scope of any slot (see ApiKeyScopes.FocusGuard) - the extension only ever polls
    /// GET /api/timerstate to decide whether to block, it never reads or writes anything else.
    /// Null = no key generated, or revoked.
    /// </summary>
    public string? FocusGuardApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation of <see cref="FocusGuardApiKeyHash"/> - display-only, same as ApiKeyCreatedAt.</summary>
    public DateTime? FocusGuardApiKeyCreatedAt { get; set; }
    /// <summary>
    /// SHA-256 hash of the per-user API key for the studylife-focustunes browser extension - same
    /// shape and reasoning as <see cref="FocusGuardApiKeyHash"/>, a SEPARATE credential/slot from
    /// the other five, and the same narrow scope (see ApiKeyScopes.FocusTunes): only
    /// GET /api/timerstate + Whoami. Switches a configured Spotify playlist when a focus session
    /// starts/ends - all of the actual playback control happens against Spotify's own API using
    /// the user's own separately-obtained Spotify OAuth token, never through this server. Null =
    /// no key generated, or revoked.
    /// </summary>
    public string? FocusTunesApiKeyHash { get; set; }
    /// <summary>Timestamp of the (last) generation of <see cref="FocusTunesApiKeyHash"/> - display-only, same as ApiKeyCreatedAt.</summary>
    public DateTime? FocusTunesApiKeyCreatedAt { get; set; }
    /// <summary>
    /// Permanent, per-user token for the subscribable ICS calendar feed
    /// (GET /api/sessions/ics?calendarToken=...). Unlike ApiKeyHash, stored in PLAINTEXT
    /// (no hash) - the user must be able to read the subscription URL again from the setup page
    /// at any time, to re-subscribe to it in a different calendar app, not just see it once
    /// when generating it, like the API key. Generated lazily on the first GET instead of
    /// on user creation, so a user who never uses the feature doesn't get a token either.
    /// Replaces the former single, global CalendarTokenProvider token, which accidentally
    /// showed every caller the same calendar (that of the first-registered user).
    /// </summary>
    public string? CalendarToken { get; set; }
    public DateTime? CalendarTokenCreatedAt { get; set; }
    /// <summary>
    /// Explicit owner flag (audit finding A15/A2 fix) - the only user who may use the raw
    /// backup/restore/restart endpoints (BackupController) and sees the corresponding setup UI
    /// (AuthController.GetAccountInfo). Previously derived implicitly as "the AuthUser with the
    /// lowest Id" in two separate places - a restore of a foreign backup, demo seeding, or a
    /// future user-deletion feature could silently move it. See
    /// Services/OwnershipService.cs for assignment (registration, demo seeding) and the
    /// self-healing fallback if no row has this set (e.g. a restored pre-flag backup).
    /// Default false; the migration AddAuthUserIsOwner backfills the existing lowest-Id user.
    /// </summary>
    public bool IsOwner { get; set; }
}

/// <summary>
/// A registered WebAuthn passkey (phase 2 of the multi-user rework). A user can have multiple
/// passkeys (e.g. phone + laptop); CredentialId/PublicKey come verified from the
/// Fido2NetLib attestation, SignCount serves as replay protection on login (a counter that
/// jumps backward indicates a cloned authenticator and is rejected with 401).
/// </summary>
public class PasskeyCredentialEntity
{
    public int Id { get; set; }
    /// <summary>FK to AuthUserEntity - deliberately without an FK constraint, the same loose pattern as everywhere else.</summary>
    public int AuthUserId { get; set; }
    /// <summary>Credential ID generated by the authenticator, unique worldwide (unique index).</summary>
    public byte[] CredentialId { get; set; } = [];
    /// <summary>COSE-encoded public key that Fido2NetLib verifies login signatures against.</summary>
    public byte[] PublicKey { get; set; } = [];
    public uint SignCount { get; set; }
    /// <summary>Freely editable display name ("Alex's iPhone"); null = UI shows a fallback.</summary>
    public string? DeviceLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Null = waiting for approval by an already logged-in device (additional passkey
    /// path, register/begin-additional) and cannot be used to log in in this state
    /// (see AuthController.LoginComplete) - a stolen/reused
    /// session token alone is thus not enough to permanently plant one's own access means.
    /// For the open initial registration (claiming a legacy user or a new
    /// user) set immediately to CreatedAt, because there is no "other" device there that could approve it.</summary>
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>
/// A login session (phase 2): issued by POST /api/auth/login/complete or
/// /register/complete, validated by the gate in Program.cs via the X-Session-Token header.
/// TokenHash is the SHA-256 of the plaintext token - the plaintext leaves the server exactly
/// once (login response) and is never stored. ExpiresAt slides on every valid
/// request to "now + 90 days", HardExpiresAt (IssuedAt + 180 days) caps that hard: so even
/// daily use forces a fresh passkey login after 180 days at the latest.
/// </summary>
public class AuthSessionEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime HardExpiresAt { get; set; }
    public DateTime LastUsedAt { get; set; }
}

/// <summary>
/// A one-time emergency login code (POST api/auth/recovery/*): only the SHA-256 hash is stored in
/// the DB (unique index - the login lookup identifies the user directly via the hash,
/// hence deliberately WITHOUT a query filter like the other auth tables). UsedAt != null =
/// consumed; recovery/generate deletes the user's entire previous set.
/// </summary>
public class RecoveryCodeEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

/// <summary>
/// Single-use registration invite (audit finding A10 - registration gate). Only ever created by
/// the instance owner (POST /api/auth/invites, see AuthController), and only meaningful when
/// Registration:Mode=invite (RegistrationGateService) - a leftover row from before a mode switch
/// is simply never looked up in "open"/"closed" mode. Like every other credential in this schema
/// (AuthSessionEntity.TokenHash, AuthUserEntity.ApiKeyHash, RecoveryCodeEntity.CodeHash), only the
/// SHA-256 hash is stored - the plaintext token leaves the server exactly once, in the create
/// response, and travels to the invitee as a "/register?invite=&lt;token&gt;" link.
/// UsedAt/UsedByUserId are set together, atomically, by RegistrationGateService.TryConsumeInviteAsync
/// (a single "UPDATE ... WHERE UsedAt IS NULL" against the unique TokenHash index) at
/// register/complete, not at register/begin - so an abandoned/failed registration attempt never
/// burns the invite, and two concurrent register/complete calls racing on the same token can
/// never both succeed.
/// </summary>
public class AuthInviteEntity
{
    public int Id { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>The owner who generated this invite (no FK, same loose pattern as everywhere else).</summary>
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Defaults to CreatedAt + 7 days (RegistrationGateService.InviteLifetime).</summary>
    public DateTime ExpiresAt { get; set; }
    /// <summary>Null = not yet used. Set exactly once, together with UsedByUserId.</summary>
    public DateTime? UsedAt { get; set; }
    /// <summary>The newly created AuthUser that consumed this invite - null until UsedAt is set.</summary>
    public int? UsedByUserId { get; set; }
}

/// <summary>
/// Instance-wide configuration, exactly one row (fixed Id 1, see SystemSecretsService) - no
/// AuthUserId, no query filters. Replaces the former file-based vapid-keys.json/
/// setup-secret.txt (scalability branch): multiple pods without a guaranteed shared volume
/// would otherwise each have generated their own, diverging values.
/// </summary>
public class SystemSecretsEntity
{
    public int Id { get; set; }
    public string? VapidPublicKey { get; set; }
    public string? VapidPrivateKey { get; set; }
    public string? VapidSubject { get; set; }
    public string? SetupSecretCode { get; set; }
}

public class StudySessionEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? Topic { get; set; }
    public string? Notes { get; set; }
    public bool IsCompleted { get; set; }
    public int TimerModeId { get; set; }
    public string? RecurrenceGroupId { get; set; }
}

public class UserSettingsEntity
{
    public int Id { get; set; }
    /// <summary>No longer a singleton row since phase 1 of the multi-user rework, but one row PER user.</summary>
    public int AuthUserId { get; set; }
    /// <summary>
    /// Optimistic-concurrency counter (audit S4/S5): starts at 0 for a freshly created row and
    /// increments by exactly 1 on every successful SettingsController.Save PUT. GET always
    /// returns the current value (UserSettingsDto.Version); a PUT that supplies it back gets
    /// rejected with 409 Conflict unless it still matches the row's current value - this is
    /// what closes the "two devices doing full read-modify-write PUTs silently revert each
    /// other's toggles" race (last-writer-wins across ~35 fields, worsened by the settings
    /// GET cache's up-to-15s TTL, see SettingsController.Get). Deliberately a plain,
    /// app-incremented int column - not an EF IsConcurrencyToken()/rowversion - specifically so
    /// the precondition can stay OPTIONAL: a PUT that omits Version entirely (older clients,
    /// Home Assistant, ad-hoc scripts against the API) must keep today's plain
    /// last-writer-wins behavior untouched, see SettingsController.Save. Not bumped by
    /// BackupController.TouchLastBackupDownloadAt or SettingsController's progress-share
    /// endpoints - those already write only their own narrow field(s) outside the normal PUT
    /// (same "set directly, not via the normal settings PUT" rationale as
    /// LastBackupDownloadAt/ProgressShareEnabled below), so they don't participate in the
    /// full-row race this field targets.
    /// </summary>
    public int Version { get; set; }
    public string SelectedCourseIds { get; set; } = "1,2,3,4"; // comma-separated
    public string CompletedCourseIds { get; set; } = ""; // comma-separated
    public string Theme { get; set; } = "dark";
    /// <summary>
    /// Curated accent color (preset key, not raw hex values) - separate from the dark/light theme,
    /// but stored server-side the same way and synced cross-device via AppStateService
    /// (see applyAccent in index.html + the --accent/--accent2 presets in base.css).
    /// </summary>
    public string AccentColor { get; set; } = "coral";
    public bool AutoSwitchFocus { get; set; } = true;
    public int AutoSwitchMinutesBefore { get; set; } = 2;
    public string MotivationalStyle { get; set; } = "claude";
    public string SessionReminderMinutes { get; set; } = "60,30,10,5,3,2,1";
    public string CourseGoalReminderDays { get; set; } = "14,7,3,1,0";
    public int InactivityThresholdDays { get; set; } = 5;
    public int StudyWindowStartHour { get; set; } = 8;
    public int StudyWindowEndHour { get; set; } = 21;
    public string StudyDays { get; set; } = "0,1,2,3,4,5,6";
    public DateTime? TargetGraduationDate { get; set; }
    /// <summary>
    /// Custom timer modes as a JSON array (IDs starting at 100, built-ins are 1-5).
    /// JSON instead of the usual comma lists, because mode names may contain commas.
    /// </summary>
    public string CustomTimerModes { get; set; } = "";
    /// <summary>
    /// Weekly study-hours goal (min/max range), replaces the previously hardcoded
    /// 25-30h/week range. The reference value for forecast/pace ratio is still the mean of
    /// both values, see BuildForecast in Index.razor/Stats.razor.
    /// </summary>
    public int WeeklyGoalMinHours { get; set; } = 25;
    public int WeeklyGoalMaxHours { get; set; } = 30;
    /// <summary>
    /// Monthly study-hours goal (min/max range), configurable independently of the weekly goal -
    /// replaces the monthly target previously derived automatically from the weekly goal
    /// (weeks-in-month × weekly goal) on the dashboard.
    /// </summary>
    public int MonthlyGoalMinHours { get; set; } = 100;
    public int MonthlyGoalMaxHours { get; set; } = 130;
    /// <summary>
    /// Granular push toggles per reminder category, independent of the general notification opt-in
    /// (browser permission). All default true, so existing users keep getting all pushes
    /// until they actively opt out of individual categories.
    /// </summary>
    public bool SessionRemindersEnabled { get; set; } = true;
    public bool CourseGoalRemindersEnabled { get; set; } = true;
    public bool InactivityRemindersEnabled { get; set; } = true;
    public bool AchievementNotificationsEnabled { get; set; } = true;
    public bool WeeklyReportEnabled { get; set; } = true;
    /// <summary>
    /// Daily motivation push (RunDailyMotivationAsync). Unlike the other toggles,
    /// defaults to false: a NEW, daily-firing category shouldn't push existing users unasked -
    /// the "default true" argument above only applies to already-known push kinds.
    /// </summary>
    public bool DailyMotivationEnabled { get; set; }
    /// <summary>
    /// Reminder per individual course (RunPerCourseInactivityCheckAsync): fires when an actively
    /// enrolled course (SelectedCourseIds) hasn't had a session in a long time, but the user
    /// keeps studying overall - unlike the global inactivity reminder above, which only
    /// fires on COMPLETE silence. Default false (opt-in, new category).
    /// </summary>
    public bool PerCourseInactivityRemindersEnabled { get; set; }
    /// <summary>
    /// Timestamp of the last successful manual backup download (GET /api/backup/database,
    /// SetupBackupCard.razor) - set directly in BackupController, not via the normal
    /// settings PUT. Null = never downloaded. Separate from the weekly automatic
    /// server dump (BackgroundTaskService/DatabaseBackupService), which doesn't protect the same
    /// device against complete device loss and is therefore queried separately on the dashboard.
    /// </summary>
    public DateTime? LastBackupDownloadAt { get; set; }
    /// <summary>
    /// Id of the active custom study program (StudyProgramEntity). Null = the built-in
    /// study program (CourseCatalog.AppliedAICourses) is active - default for
    /// existing users, so they see no behavior change.
    /// </summary>
    public int? ActiveStudyProgramId { get; set; }
    /// <summary>
    /// Read-only progress link active? Set exclusively via SettingsController.Enable/
    /// Disable/RegenerateProgressShareToken, NOT via SettingsController.Save - same
    /// rationale as LastBackupDownloadAt (dedicated write path instead of PUT).
    /// </summary>
    public bool ProgressShareEnabled { get; set; }
    /// <summary>Permanent token for GET /api/progress/shared/{token}. Null = disabled or
    /// never activated - DisableProgressShare deletes it as well, so a leaked link doesn't
    /// become valid again through disable+re-enable (see SettingsController).</summary>
    public string? ProgressShareToken { get; set; }
    /// <summary>
    /// Warns if the current study streak can still break today (RunStreakRiskCheckAsync).
    /// Default false (opt-in, new potentially intrusive nudge category).
    /// </summary>
    public bool StreakRiskRemindersEnabled { get; set; }
    /// <summary>
    /// Gentle mid-week nudge when the weekly goal is significantly lagging
    /// (RunWeeklyGoalNudgeCheckAsync). Default false (opt-in, new category).
    /// </summary>
    public bool WeeklyGoalNudgeEnabled { get; set; }
    /// <summary>
    /// "Almost done" nudge for courses with ≥85% topic progress and no recent session
    /// (RunCourseAlmostDoneCheckAsync). Default false (opt-in, new category).
    /// </summary>
    public bool CourseAlmostDoneRemindersEnabled { get; set; }
    /// <summary>
    /// Reminds shortly before the user's historically most productive time of day
    /// (RunBestStudyTimeCheckAsync). Default false (opt-in, new category).
    /// </summary>
    public bool BestStudyTimeRemindersEnabled { get; set; }
    /// <summary>
    /// Gentle, short comeback nudge after EXACTLY 1 day of pause (RunComebackNudgeCheckAsync) -
    /// deliberately separate from InactivityRemindersEnabled (only fires from InactivityThresholdDays,
    /// default 5) and phrased noticeably more gently. Default false (opt-in, new category).
    /// </summary>
    public bool ComebackNudgeEnabled { get; set; }
    /// <summary>
    /// Instant feedback on a new personal record (longest single session so far), triggered
    /// directly in the request handler of SessionsController.Create/Update instead of via the
    /// BackgroundTaskService polling cycle. Default false (opt-in, new category).
    /// </summary>
    public bool NewRecordNotificationsEnabled { get; set; }
    /// <summary>
    /// Monthly recap push (RunMonthlyReportAsync), analogous to WeeklyReportEnabled. Default true,
    /// because it extends the same already-established "does the user already want this" category
    /// instead of introducing a completely new, potentially intrusive kind of push (unlike the
    /// other opt-in toggles in this batch).
    /// </summary>
    public bool MonthlyReportEnabled { get; set; } = true;
}

public class PushSubscriptionEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    // Null for legacy records from before this column (migration AddPushSubscriptionDeviceInfo) -
    // the UI then deliberately shows no "X days ago" instead of displaying a made-up date.
    public DateTime? CreatedAt { get; set; }
    // User-Agent header from the last subscribe call, only for the device display in the FAB
    // (rough browser/OS detection via substring check, see PushDeviceManager.razor).
    public string? UserAgent { get; set; }

    // Delivery channel: "webpush" (VAPID, browser/PWA - all existing rows) or "apns"
    // (native iOS app shell, see ApnsSender). For "apns", ApnsToken carries the device token;
    // Endpoint is set to the synthetic value "apns:<token>", so the existing
    // unique index (AuthUserId, Endpoint), the dedup logic, and the EndpointHash of the
    // device list keep working unchanged. P256dh/Auth stay empty.
    public string Channel { get; set; } = ChannelWebPush;
    public string? ApnsToken { get; set; }

    public const string ChannelWebPush = "webpush";
    public const string ChannelApns = "apns";
}

/// <summary>
/// Persistently stores reminders already sent, so that after a server restart
/// no duplicates or missing reminders occur.
/// </summary>
public class SentReminderEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    /// <summary>e.g. "42:reminder5" - SessionId:reminderAt</summary>
    public string Key { get; set; } = "";
    public DateTime SentAt { get; set; }
}

/// <summary>
/// Outbox for the studylife-ai key registration (audit A7): SettingsController's ai-api-key
/// generate/revoke enqueue a row here BEFORE attempting immediate delivery via AiProxyClient -
/// if studylife-ai is unreachable at that moment, the plaintext (register) or the intent
/// (revoke) would otherwise be lost forever and the two databases would silently disagree.
/// BackgroundTaskService.RunAiKeyOutboxAsync drains it with backoff; a row is deleted only once
/// AiProxyClient confirms delivery. Deliberately NO query filter (like AuthSessionEntity/
/// RecoveryCodeEntity/SystemSecretsEntity) - the drain runs table-wide across all users in one
/// tick, not scoped to a single request's current user.
/// </summary>
public class AiKeyOutboxEntity
{
    public const string ActionRegister = "register";
    public const string ActionRevoke = "revoke";

    public int Id { get; set; }
    /// <summary>No FK - same loose pattern as everywhere else in this file.</summary>
    public int AuthUserId { get; set; }
    /// <summary>ActionRegister or ActionRevoke - which SettingsController endpoint enqueued this row.</summary>
    public string Action { get; set; } = "";
    /// <summary>
    /// Plaintext of the newly generated AI key, ONLY for Action="register" (null for "revoke").
    /// Transient by design: studylife-ai can't retrieve the key later (only its hash lives in
    /// AuthUserEntity.AiApiKeyHash), so the plaintext must be carried somewhere until delivery
    /// confirms - the row (and thus the plaintext) is deleted immediately on success. The
    /// trade-off: an undelivered row keeps the plaintext at rest in this table for as long as
    /// studylife-ai stays unreachable, instead of only existing in-memory for one request.
    /// </summary>
    public string? AiApiKeyPlaintext { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}

public class NoteEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int? CourseId { get; set; }
    public int? SessionId { get; set; }
    /// <summary>When true, Content is Markdown source (rendered client-side via Markdig) instead
    /// of plain text. Defaults to false so every pre-existing note keeps rendering exactly as
    /// before.</summary>
    public bool IsMarkdown { get; set; }
    /// <summary>Origin URL for notes created from external capture (e.g. the studylife-capture
    /// browser extension) - null for every note created directly in StudyLife itself. Purely
    /// informational (a "where did this come from" link), not used for any lookup/uniqueness.</summary>
    public string? SourceUrl { get; set; }
    /// <summary>Set once BackgroundTaskService.CaptureEnrichment has run studylife-ai's
    /// POST /internal/enrich-capture for this note - either because it succeeded, or because
    /// EnrichmentAttempts reached BackgroundTaskService.CaptureEnrichment.MaxEnrichmentAttempts
    /// (see that field's comment for why bounded retry, not indefinite or single-shot). Null
    /// for every note that was never a capture (SourceUrl null) or hasn't finished retrying yet -
    /// the query filter for "still needs enrichment" is SourceUrl != null &amp;&amp;
    /// EnrichedAt == null (EnrichmentAttempts is checked separately, in code, since SQLite/
    /// Postgres would need the constant duplicated into the query otherwise).</summary>
    public DateTime? EnrichedAt { get; set; }
    /// <summary>How many times BackgroundTaskService.CaptureEnrichment has attempted this note
    /// (successful or not) - caps retries after a transient failure (e.g. studylife-ai
    /// unreachable during a deployment rollout) instead of giving up after one attempt forever,
    /// while still avoiding an indefinite retry storm against a genuinely broken integration.</summary>
    public int EnrichmentAttempts { get; set; }
    /// <summary>When EnrichmentAttempts was last incremented - enforces a minimum backoff
    /// between retries (see BackgroundTaskService.CaptureEnrichment.MinRetryBackoff) so a
    /// transient outage gets a realistic amount of time to recover before the next attempt,
    /// instead of burning through MaxEnrichmentAttempts within seconds at the normal tick
    /// cadence.</summary>
    public DateTime? LastEnrichmentAttemptAt { get; set; }
    /// <summary>Comma-separated short keywords from studylife-ai's tag suggestion (capture
    /// enrichment only, see EnrichedAt) - null until enrichment runs, or if it produced none.
    /// Plain comma-separated string, same convention as CourseGoalDto's own Tag field elsewhere
    /// in this schema - not worth a separate table for a handful of short strings.</summary>
    public string? Tags { get; set; }
    /// <summary>One-sentence AI-generated summary from capture enrichment (see EnrichedAt) -
    /// null until enrichment runs, or if it produced none.</summary>
    public string? Summary { get; set; }
    /// <summary>Comma-separated ids of existing notes studylife-ai found similar to this capture
    /// (see EnrichedAt) - null until enrichment runs, or if it found none. Same comma-separated
    /// convention as Tags/UserSettingsEntity.SelectedCourseIds; a note whose id appears here may
    /// since have been deleted - the UI resolves ids against the client's already-loaded notes
    /// list and simply skips any that no longer exist, rather than this needing to be kept in
    /// sync with deletions.</summary>
    public string? RelatedNoteIds { get; set; }
}

/// <summary>
/// Study goal per course: desired completion date + optional note on completion.
/// CourseId is unique - one goal per course, upserted via PUT.
/// </summary>
public class CourseGoalEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public DateTime? TargetDate { get; set; }
    public string? CompletionNote { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Final grade, German grading system (1.0 = best, 5.0 = failed).</summary>
    public decimal? Grade { get; set; }
    /// <summary>Comma-separated list of checked-off topic names from CourseCatalog.Topics.</summary>
    public string CompletedTopics { get; set; } = "";
    public string? Tag { get; set; }
}

/// <summary>
/// Singleton row (like UserSettingsEntity) with the last focus timer state reported
/// by the client. The client only pushes here on state changes, not
/// every second - see TimerStateDto in StudyLife.Shared.
/// </summary>
public class TimerStateEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int? SessionId { get; set; }
    public bool IsRunning { get; set; }
    public bool IsBreak { get; set; }
    public int CurrentRound { get; set; }
    public int TimerModeId { get; set; }
    public DateTime? PhaseEndsAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Last accepted TimerStateDto.ClientSequence (audit S6), so a stale/out-of-order PUT
    /// (see TimerStateController.Save) can be detected across separate requests, not just within
    /// one process's memory. Null = no sequence-carrying PUT has ever landed yet (fresh row, or
    /// every PUT so far came from a non-sequence-aware pusher like Home Assistant) - the very
    /// next sequence-carrying PUT is then always accepted (nothing to compare against).</summary>
    public long? LastClientSequence { get; set; }

    /// <summary>ActivityKit push token of the currently running live activity (native iOS app,
    /// paid profile) - hex-encoded like ApnsToken. Deliberately NOT part of TimerStateDto/
    /// Save(): this field is set exclusively via its own liveactivity-token endpoint,
    /// so that the normal state push from TimerService (start/pause/stop, runs on
    /// EVERY platform incl. web) doesn't overwrite it with null on every call. The worker
    /// (BackgroundTaskService) clears it itself when the session ends.</summary>
    public string? LiveActivityPushToken { get; set; }
}

/// <summary>
/// Custom study program (POST /api/studyprograms). The built-in
/// study program (CourseCatalog.AppliedAICourses) deliberately has NO DB row - it is
/// represented via UserSettings.ActiveStudyProgramId == null; only
/// user-created programs live here.
/// </summary>
public class StudyProgramEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// Purely MANUAL completion flag (PUT /api/studyprograms/{id}/completed) - is NEVER
    /// set automatically, not even at 100% ECTS. Completed study programs remain
    /// selectable in the switcher (looking back at their frozen history), they are just
    /// marked there with a checkmark.
    /// </summary>
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Elective group of a custom study program - same semantics as
/// CourseCatalog.GroupEctsQuotas: at most EctsQuota ECTS are credited per group,
/// regardless of how many courses in the group are completed.
/// </summary>
public class CourseGroupEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int StudyProgramId { get; set; }
    public string Name { get; set; } = "";
    public int EctsQuota { get; set; }
}

/// <summary>
/// Course of a custom study program (deliberately not named "Course", to
/// avoid confusion with the static CourseDto catalog). Delivered via
/// GET /api/courses as a CourseDto when the study program is active -
/// the client course grids consume both sources identically. The DTO Id is
/// shifted by StudyProgramCatalog.CustomCourseIdOffset in the process, so it never
/// collides with the built-in catalog's Ids (1-62) - Selected/CompletedCourseIds
/// thus stay unique per study program.
/// </summary>
public class CustomCourseEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int StudyProgramId { get; set; }
    public int Semester { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Color { get; set; } = "#6C5CE7";
    public string Icon { get; set; } = "📚";
    public int Ects { get; set; } = 5;
    /// <summary>FK to CourseGroupEntity. Null = mandatory module without an elective group.</summary>
    public int? CourseGroupId { get; set; }
    /// <summary>Comma-separated topic list (same convention as CourseGoalEntity.CompletedTopics).</summary>
    public string Topics { get; set; } = "";
}

/// <summary>
/// Reusable template for quickly created sessions (POST /api/sessiontemplates), e.g.
/// "Analysis lecture, 90 min, Mondays 10:00". DefaultWeekday/DefaultStartTime are
/// automatically taken from the StartTime of the originating session when the template is
/// created (see Calendar.SessionTemplates.razor.cs) - a pure UI display/suggestion aid, NOT
/// enforced when applied (the day stays whatever the user clicked in the calendar).
/// </summary>
public class SessionTemplateEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public string Name { get; set; } = "";
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public int DurationMinutes { get; set; } = 60;
    public string? Topic { get; set; }
    /// <summary>0=Sunday..6=Saturday (System.DayOfWeek values), same convention as UserSettingsEntity.StudyDays.</summary>
    public int? DefaultWeekday { get; set; }
    public TimeSpan? DefaultStartTime { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Small link/resource collection per course (lecture slides URL, course website, book link, ...),
/// manageable via the setup page. CourseId deliberately does NOT reference a specific
/// course table via FK, but the same shared integer ID space as StudySessionEntity.CourseId
/// and CourseGoalEntity.CourseId - that covers both the built-in catalog (CourseCatalog, Ids
/// 1-62) and custom courses (CustomCourseEntity, Ids starting at StudyProgramCatalog.
/// CustomCourseIdOffset) uniformly, without needing two separate resource tables.
/// </summary>
public class CourseResourceEntity
{
    public int Id { get; set; }
    public int AuthUserId { get; set; }
    public int CourseId { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
