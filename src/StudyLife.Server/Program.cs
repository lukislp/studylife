using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.OpenApi;
using StudyLife.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Audit finding D2: formal API contract. Serves the live document at /openapi/v1.json
// (app.MapOpenApi() below) - controllers return typed ActionResult<T>, so DTO component schemas
// (StudySessionDto, NoteDto, ...) fall out with their real names for free. The security-scheme
// document transformer documents the two real header credentials (X-Session-Token/X-Api-Key) -
// see StudyLifeOpenApiSecuritySchemeTransformer. This is the RUNTIME endpoint only; the
// COMMITTED contract artifact consumers pin against (docs/api/openapi.json) is a separate
// build-time generation step - see StudyLife.Server.csproj's OpenApiDocumentsDirectory.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<StudyLifeOpenApiSecuritySchemeTransformer>();
    // Audit finding D2 follow-up: the default generation leaves every DTO's "required" array
    // empty (it derives required-ness from C# `required`-keyword/constructor-parameter syntax,
    // which no DTO in StudyLife.Shared/Dtos.cs uses) - see
    // StudyLifeOpenApiRequiredPropertiesTransformer for the CLR-nullability-based fix and why.
    options.AddSchemaTransformer<StudyLifeOpenApiRequiredPropertiesTransformer>();
});

// Real AuthenticationHandler + authorization policies (audit finding A3) replacing the former
// hand-rolled inline middleware - see StudyLifeAuthorizationPolicies for the policy design and
// StudyLifeAuthenticationHandler for credential resolution (session token / API key / ICS
// calendar token, in that priority). Registered here so it's available for
// app.MapControllers().RequireAuthorization(...) etc. further below.
builder.Services.AddStudyLifeAuthentication();

// Kestrel itself only ever listens on plain HTTP:8080 (nginx/NPM terminate TLS in front of
// it, see the UseHttpsRedirection() comment below) - UseHttpsRedirection() can't infer an
// HTTPS port from that on its own and logs "Failed to determine the https port for redirect"
// on every request it would otherwise redirect. 443 is the public HTTPS port every deploy
// target's reverse proxy actually presents to clients (NPM externally, nginx-gateway/Tailscale
// Funnel for the scalability branch) - stating it explicitly keeps the redirect actually
// functional instead of just silencing the symptom.
builder.Services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);

// Encrypted hop ingress-nginx -> Kestrel (scalability branch, K8s): WebBackendTls__CertPath/
// KeyPath (see k8s/04-web.yaml) point to a secret issued by cert-manager from the
// cluster-internal CA and mounted in (k8s/07b-cert-manager-issuers.yaml). Only set in
// K8s operation - Pi/docker-compose.yml continues to run unchanged purely over HTTP:8080 (ASPNETCORE_URLS),
// there nginx terminates TLS directly in front of it. Explicit ConfigureKestrel Listen() calls
// OVERWRITE (not add to) the endpoints derived from ASPNETCORE_URLS - hence here
// the existing HTTP:8080 listener is explicitly listed too, otherwise 8080 would
// disappear without replacement when 8443 is set, and probes/Uptime Kuma (still plain HTTP) would break.
var webBackendTlsCertPath = builder.Configuration["WebBackendTls:CertPath"];
var webBackendTlsKeyPath = builder.Configuration["WebBackendTls:KeyPath"];
if (!string.IsNullOrEmpty(webBackendTlsCertPath) && !string.IsNullOrEmpty(webBackendTlsKeyPath))
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(8080);
        options.ListenAnyIP(8443, listenOptions =>
        {
            listenOptions.UseHttps(X509Certificate2.CreateFromPemFile(webBackendTlsCertPath, webBackendTlsKeyPath));
        });
    });
}

// Scalability branch: Cache:Provider switches between the process-local in-memory cache
// (default, unchanged behavior for the Pi/docker-compose.yml) and Redis (multi-pod operation,
// see docker-compose.scale.yml/k8s/) - analogous to the Database:Provider switch below.
// AddDistributedMemoryCache() implements IDistributedCache (the same interface that
// CacheHelper.GetOrSetAsync + SettingsController/SessionsController/CoursesController as well as the
// WebAuthn challenge cache in AuthController use) purely process-locally, so for
// the single-instance case it's bit-for-bit the same behavior as the former AddMemoryCache()/
// IMemoryCache, just behind the interface that Redis also serves. Multiple pods would still see
// independent caches with this default - only Redis makes the cache, the version counters, AND the
// challenge cache truly consistent across pods (the latter is the reason why
// login/registration without Redis doesn't work reliably with multiple pods, see AuthController.cs).
var cacheProvider = builder.Configuration["Cache:Provider"] ?? "Memory";
var isRedisCache = string.Equals(cacheProvider, "Redis", StringComparison.OrdinalIgnoreCase);
if (isRedisCache)
{
    var redisConnectionString = builder.Configuration["Cache:ConnectionString"]
        ?? throw new InvalidOperationException(
            "Cache:ConnectionString (bzw. ENV Cache__ConnectionString) muss gesetzt sein, wenn Cache:Provider=Redis.");
    var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnectionString);
    if (redisOptions.Ssl)
    {
        // The Redis certificate comes from our own internal CA (k8s/07b-cert-manager-issuers.yaml),
        // which would otherwise be unknown to the process - the goal is encryption against eavesdropping
        // on the cluster network, not protection against an already-compromised pod (same trade-off as
        // with ingress-nginx -> backend pods, proxy-ssl-verify deliberately not set to "on").
        redisOptions.CertificateValidation += (_, _, _, _) => true;
    }
    builder.Services.AddStackExchangeRedisCache(opt => opt.ConfigurationOptions = redisOptions);

    // Separate IConnectionMultiplexer singleton registration ONLY in the Redis branch: AddStackExchangeRedisCache
    // keeps its own internal connection, but RedisVersionCounter (SessionHistoryCacheVersion/
    // SettingsCacheVersion below) needs its own for StringIncrementAsync (INCR).
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions));

    // Two functionally separate counters need two separate Redis keys - hence constructed here via
    // a factory with a fixed key instead of registering IVersionCounter itself as a DI singleton
    // (that wouldn't allow two different instances for sessions/settings).
    builder.Services.AddSingleton(sp => new SessionHistoryCacheVersion(
        new RedisVersionCounter(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), "version:sessionhistory")));
    builder.Services.AddSingleton(sp => new SettingsCacheVersion(
        new RedisVersionCounter(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), "version:settings")));

    // DataProtection key ring persisted to Redis, ONLY in the Redis branch - same reasoning as
    // every other "needs a shared coordination point across replicas" feature above. Without this,
    // ASP.NET Core silently falls back to a local, ephemeral, unencrypted key ring per pod
    // (Microsoft.AspNetCore.DataProtection's documented default when nothing is configured) - a
    // request round-robined to a different pod than the one that issued a given auth cookie/
    // antiforgery token then fails to validate it. Found live via recurring FileSystemXmlRepository/
    // XmlKeyManager warnings in the aggregated cluster logs (studylife-mcp Loki dashboard) - NOT
    // caught before because this only manifests with 2+ replicas actually receiving traffic.
    // Separate dedicated connection (like the IConnectionMultiplexer singleton above), not the DI
    // singleton itself - that's only resolvable after builder.Build().
    //
    // The key material itself is additionally encrypted at rest (ProtectKeysWithCertificate) using
    // a dedicated cert-manager certificate (k8s/04-web.yaml, same internal CA as
    // studylife-web-backend-tls but a separate cert - this one is mounted into both web AND
    // worker, since both run this same DataProtection setup, unlike the web-only TLS cert).
    // Without this, ASP.NET Core stores the raw key material as unencrypted XML in Redis - found
    // live via the matching XmlKeyManager "No XML encryptor configured" warning that appeared the
    // moment the Redis persistence above went in. Required (not optional-with-fallback) in this
    // branch: silently falling back to unencrypted-in-Redis would be a worse and less visible
    // outcome than failing fast at startup, same reasoning as the Cache:ConnectionString check.
    var dataProtectionCertPath = builder.Configuration["DataProtection:CertPath"]
        ?? throw new InvalidOperationException(
            "DataProtection:CertPath (bzw. ENV DataProtection__CertPath) muss gesetzt sein, wenn Cache:Provider=Redis.");
    var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"]
        ?? throw new InvalidOperationException(
            "DataProtection:KeyPath (bzw. ENV DataProtection__KeyPath) muss gesetzt sein, wenn Cache:Provider=Redis.");
    builder.Services.AddDataProtection()
        .SetApplicationName("StudyLife")
        .PersistKeysToStackExchangeRedis(StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions), "dataprotection:keys")
        .ProtectKeysWithCertificate(
            X509Certificate2.CreateFromPemFile(dataProtectionCertPath, dataProtectionKeyPath));
}
else
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton(_ => new SessionHistoryCacheVersion(new InMemoryVersionCounter()));
    builder.Services.AddSingleton(_ => new SettingsCacheVersion(new InMemoryVersionCounter()));
}

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// Abuse/DoS protection, NOT usage throttling: 1-2 real users plus Home Assistant
// (polling every ~30s) and the Blazor client together never even come close to
// 300 requests/minute - so a legitimate client never sees a 429. Fixed window instead of
// sliding: with such generous limits, the 2x burst at the window boundary doesn't matter, and in return
// it's the cheapest limiter (one counter per partition). Partitioned per client IP;
// that's accurate here because UseForwardedHeaders below runs BEFORE UseRateLimiter, and
// RemoteIpAddress is therefore already the real IP reported by nginx via X-Forwarded-For.
// Static assets (_framework/*, index.html, ...) are deliberately left unlimited - Blazor
// loads dozens of files in parallel on startup, that must never be throttled.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Audit finding O6: this also covers /healthz/ready + /healthz/live (HealthController) -
        // deliberately unlimited like every other non-/api path, not just incidentally. Probe
        // traffic reaches the pod directly (kube-probe never goes through the ingress hop that
        // populates X-Forwarded-For), so its "client IP" is the probing node's address - many
        // pods on the same node would otherwise share one rate-limit partition and could throttle
        // each other's probes. Living outside /api sidesteps that question entirely instead of
        // needing a dedicated exemption.
        if (!context.Request.Path.StartsWithSegments("/api"))
            return RateLimitPartition.GetNoLimiter("no-limit");

        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Emergency login with a one-time code: noticeably stricter than the generic API limit -
        // the endpoint is unauthenticated and a 12-character code is the only secret,
        // so brute-force throttling is the actual line of defense here.
        if (context.Request.Path.StartsWithSegments("/api/auth/recovery/login"))
        {
            return RateLimitPartition.GetFixedWindowLimiter($"recovery|{clientIp}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                });
        }

        return RateLimitPartition.GetFixedWindowLimiter($"api|{clientIp}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromSeconds(60),
                QueueLimit = 0
            });
    });
});

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "app_data", "studylife.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// Scalability branch: Database:Provider switches between the single SQLite file (default,
// unchanged behavior for the Pi/docker-compose.yml) and Postgres (for multi-pod operation,
// see docker-compose.scale.yml/k8s/). Postgres needs its OWN migration history
// (SQLite and Postgres SQL are not interchangeable) - for that, the subclass StudyLifeDbPostgres
// (see StudyLifeDb.cs). The SQLite branch deliberately continues to register the base class
// StudyLifeDb directly (unchanged from before this branch) - all 38 existing migrations
// are tagged to it; a separate StudyLifeDbSqlite subclass would make them invisible to EF Core.
// Controllers/services inject only the base class StudyLifeDb either way and don't notice
// which provider is active.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var isPostgres = string.Equals(databaseProvider, "Postgres", StringComparison.OrdinalIgnoreCase);

if (!isPostgres)
{
    // Apply a staged restore (POST /api/backup/restore) - MUST happen here, before
    // AddDbContextPool and well before Migrate(): at this point no one has yet opened a
    // connection to the live DB, so the file swap is safe (no WAL/page cache mismatch).
    // Migrate() below then runs normally against the restored DB and
    // automatically brings a backup with an older schema up to the current state. Never throws -
    // any failure leaves the existing DB running untouched. Only relevant in SQLite mode -
    // the raw backup/restore feature is deliberately SQLite-only (see BackupController).
    var restoreOutcome = DatabaseRestoreService.ApplyPendingRestore(dbPath);
    if (restoreOutcome.Status != RestoreApplyStatus.NoPending)
        Console.WriteLine($"[restore] Staged database restore: {restoreOutcome.Status}"
                          + (restoreOutcome.Detail is null ? "" : $" - {restoreOutcome.Detail}"));
}

// AddDbContext instead of the former AddDbContextPool: context pooling requires a constructor
// that takes ONLY DbContextOptions - but since the multi-tenant rework, StudyLifeDb needs the
// scoped ICurrentUserAccessor for the global query filters. With 1-2 users on a Raspberry Pi,
// giving up pooling is measurably irrelevant (context construction costs microseconds).
if (isPostgres)
{
    var postgresConnectionString = builder.Configuration["Database:ConnectionString"]
        ?? throw new InvalidOperationException(
            "Database:ConnectionString (bzw. ENV Database__ConnectionString) muss gesetzt sein, wenn Database:Provider=Postgres.");
    builder.Services.AddDbContext<StudyLifeDb, StudyLifeDbPostgres>(opt => opt.UseNpgsql(postgresConnectionString));
    builder.Services.AddScoped<INoteSearchStrategy, PostgresTsvectorSearchStrategy>();
}
else
{
    builder.Services.AddDbContext<StudyLifeDb>(opt => opt.UseSqlite($"Data Source={dbPath}"));
    builder.Services.AddScoped<INoteSearchStrategy, SqliteFts5SearchStrategy>();
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
// CourseId validation (audit finding M2) - scoped like StudyLifeDb itself since it queries
// custom courses directly; see CourseResolver's doc comment for what it resolves against.
builder.Services.AddScoped<ICourseResolver, CourseResolver>();
// Shared owner check (audit A15/A2 fix) - see OwnershipService for the AuthUserEntity.IsOwner
// rationale; scoped like StudyLifeDb itself since it queries it directly.
builder.Services.AddScoped<IOwnershipService, OwnershipService>();
// Registration gate (audit finding A10) - Registration:Mode (env Registration__Mode); see
// RegistrationGateService for the open/invite/closed semantics and the bootstrap bypass.
builder.Services.AddScoped<IRegistrationGateService, RegistrationGateService>();
// APNs channel for the native app shell - permanently inactive without Apns:* configuration
// (see the ApnsSender comment), web push/VAPID is unaffected by this.
builder.Services.AddSingleton<ApnsSender>();
// studylife-ai integration - permanently inactive without StudyLifeAi:* configuration
// (see the AiProxyClient comment).
builder.Services.AddSingleton<AiProxyClient>();
// Raw DB backup/restore + the former file-based VAPID/setup-secret storage are bound to a
// local single SQLite file (online backup API, PRAGMA integrity_check, file swap) -
// deliberately NOT registered in Postgres mode (multiple pods, no guaranteed shared volume);
// BackupController reports 501 there for the raw endpoints (JSON export remains available
// provider-independently). VAPID keys/setup secret are now DB-backed anyway (SystemSecretsEntity, see below),
// no longer file-based - works identically regardless of provider.
if (!isPostgres)
{
    builder.Services.AddSingleton(new DatabaseBackupService(dbPath, builder.Environment.ContentRootPath));
    builder.Services.AddSingleton(new DatabaseRestoreService(dbPath));
}
builder.Services.AddScoped<SystemSecretsService>();
builder.Services.AddSingleton<VapidKeysHolder>();
// "Read note aloud": voices are ONNX files baked into the image (see Dockerfile), not
// checked into this repo - loading one is real work (ONNX Runtime session init), so the
// registry caches loaded voices process-wide instead of per-request. Which languages are
// actually shipped is a Dockerfile concern, not a code concern - TryGet just returns null
// for anything not present on disk, which the controller turns into a 404.
builder.Services.AddSingleton(new StudyLife.Tts.PiperVoiceRegistry(
    builder.Configuration["Tts:VoicesDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "tts-voices")));
builder.Services.AddSingleton<StudyLife.Tts.EspeakPhonemizer>();
// Voice dictation: one multilingual Whisper model (see Dockerfile), unlike PiperVoiceRegistry's
// per-language voices - loaded once at startup instead of lazily, since (unlike TTS voices)
// there's only ever the one model to load, no "which languages are actually present" question.
builder.Services.AddSingleton(new StudyLife.Stt.WhisperTranscriber(
    builder.Configuration["Stt:ModelPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "stt-model", "ggml-base.bin")));
// Worker:Enabled disables BackgroundTaskService (30s tick loop: push reminders, reports, maintenance)
// when this process is a stateless web pod in scaled operation (docker-
// compose.scale.yml/k8s/) - there the worker runs as its OWN deployment. Default true =
// today's behavior (Pi/docker-compose.yml, everything in one process), unchanged.
var workerEnabled = builder.Configuration.GetValue("Worker:Enabled", true);
var workerReplicaCount = builder.Configuration.GetValue("Worker:ReplicaCount", 1);
// Multiple worker processes cannot coordinate without Redis which one handles which user partition
// (IWorkerShardClaim below) - fail fast instead of silently producing wrong/duplicate
// results, analogous to the Cache:ConnectionString/Database:ConnectionString mandatory checks above.
if (workerReplicaCount > 1 && !isRedisCache)
    throw new InvalidOperationException(
        "Worker:ReplicaCount > 1 setzt Cache:Provider=Redis voraus (verteilte Shard-Koordination).");
// Worker:ReplicaCountSource=Kubernetes switches from a static Worker:ReplicaCount frozen at
// pod start to a LIVE query of the current Kubernetes deployment replica
// count - a prerequisite for safe HPA autoscaling of the worker (see
// IWorkerReplicaCountProvider.cs). Also needs Redis coordination, for the same reason as
// Worker:ReplicaCount > 1 above (the replica count CAN rise above 1 via HPA, even if it
// currently isn't). Default "Static" = today's behavior, unchanged.
var replicaCountSource = builder.Configuration["Worker:ReplicaCountSource"] ?? "Static";
var useKubernetesReplicaCount = string.Equals(replicaCountSource, "Kubernetes", StringComparison.OrdinalIgnoreCase);
if (useKubernetesReplicaCount && !isRedisCache)
    throw new InvalidOperationException(
        "Worker:ReplicaCountSource=Kubernetes setzt Cache:Provider=Redis voraus (verteilte Shard-Koordination).");
if (useKubernetesReplicaCount)
{
    var workerDeploymentName = builder.Configuration["Worker:DeploymentName"] ?? "studylife-worker";
    builder.Services.AddSingleton<IWorkerReplicaCountProvider>(sp => new KubernetesWorkerReplicaCountProvider(
        workerDeploymentName, workerReplicaCount, sp.GetRequiredService<ILogger<KubernetesWorkerReplicaCountProvider>>()));
}
else
{
    builder.Services.AddSingleton<IWorkerReplicaCountProvider>(new StaticWorkerReplicaCountProvider(workerReplicaCount));
}
// IWorkerShardClaim determines PER TICK which user partition this process is allowed to handle -
// a dynamic claim via Redis instead of derivation from pod name/hostname, so the same
// partitioning works identically on Kubernetes, AWS ECS, or multiple VPS instances (see
// IWorkerShardClaim.cs). Only registered in the Redis branch, otherwise StaticWorkerShardClaim
// (default constructor fallback in BackgroundTaskService, no Redis needed with 1 replica).
if (isRedisCache)
{
    builder.Services.AddSingleton<IWorkerShardClaim>(sp => new RedisWorkerShardClaim(
        sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetRequiredService<IWorkerReplicaCountProvider>()));
}
if (workerEnabled)
    builder.Services.AddHostedService<BackgroundTaskService>();

var app = builder.Build();

// Skip every startup side effect below (migration, VAPID key generation, demo reseed, setup-
// secret issuance) when EF Core's design-time tooling builds this same WebApplication just to
// resolve the DbContext model - `dotnet ef migrations has-pending-model-changes` (the CI job
// guarding audit finding O2b, see .github/workflows/ci-cd.yml) does exactly that for BOTH
// StudyLifeDb and StudyLifeDbPostgres. EF Core's HostFactoryResolver only intercepts at
// Run()/RunAsync() - every top-level statement between Build() and Run() (this whole block)
// would otherwise execute for real, including a live Database.Migrate() against the Postgres
// leg's deliberately fake/unreachable Database:ConnectionString. EF.IsDesignTime is set by the
// tooling before it invokes this file, purely for this purpose.
if (!EF.IsDesignTime)
{
    // Audit finding O2: migration OWNERSHIP. Every pod used to call Migrate() unconditionally on
    // startup - harmless with a single replica, but wrong once the worker runs as its own
    // Deployment (k8s/05-worker.yaml) alongside web (k8s/04-web.yaml): concurrent Migrate() calls
    // race each other on every rolling restart, and a worker should never own schema changes in
    // the first place. Database:Migrate (default true) is the switch - only the k8s worker
    // Deployment sets it to "false"; every other flow (single-container default,
    // docker-compose.scale.yml, the dev-cluster kind setup, and the k8s WEB Deployment itself)
    // keeps calling Migrate() exactly as before, unchanged. A non-migrating process instead waits
    // below until the migrating one has caught the schema up (WaitForPendingMigrationsAsync).
    var shouldMigrate = builder.Configuration.GetValue("Database:Migrate", true);

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
    if (shouldMigrate)
    {
        db.Database.Migrate();
    }
    else
    {
        await WaitForPendingMigrationsAsync(db);
    }
    if (!isPostgres)
    {
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        // 5s: comfortably outlasts the 30s BackgroundTaskService writes without stalling requests noticeably
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
    }

    // VAPID keys + setup secret are now DB-backed (SystemSecretsService) instead of file-based -
    // therefore must be resolved here (after Migrate(), with a working DB connection) instead of already
    // before Build(). VapidKeysHolder.Keys is populated here EXACTLY ONCE, before app.Run()
    // runs below - see the comment on VapidKeysHolder for the ordering guarantee.
    var systemSecrets = scope.ServiceProvider.GetRequiredService<SystemSecretsService>();
    scope.ServiceProvider.GetRequiredService<VapidKeysHolder>().Keys =
        await systemSecrets.EnsureVapidKeysAsync(builder.Configuration);

    // Public demo instances: wipe and re-create the demo dataset on EVERY start - the data is
    // generated relative to "today", so restarting the container is also what keeps the demo
    // looking current (see DemoSeeder). Deliberately after Migrate() and the VAPID resolution
    // above (SystemSecrets stay untouched), and never reachable without DEMO_MODE=true PLUS the
    // DEMO_MODE_CONFIRM_DATA_LOSS guard (see DemoModeGuard) - this is a full-table wipe.
    if (DemoModeGuard.IsEnabled(builder.Configuration))
    {
        await DemoSeeder.ReseedAsync(db);
        Console.WriteLine("[demo] DEMO_MODE active: database wiped and reseeded with demo data");
    }

    // Setup secret for the very first registration (AuthController.RegisterBegin) - as long as no
    // passkey exists, re-issue it on every start, so an operator who misses it on the first
    // boot finds it again in the logs on the next restart. Once registered,
    // the code is never needed again - clean it up instead of leaving it around indefinitely.
    if (await db.PasskeyCredentials.AnyAsync())
    {
        await systemSecrets.ClearSetupSecretAsync();
    }
    else
    {
        var code = await systemSecrets.EnsureSetupSecretAsync();
        Console.WriteLine("========================================================");
        Console.WriteLine("  StudyLife setup code (for the very first registration only):");
        Console.WriteLine();
        Console.WriteLine($"      {code}");
        Console.WriteLine();
        Console.WriteLine("  Enter it on /register to become the owner of this installation.");
        Console.WriteLine("========================================================");
    }
}

// nginx runs as its own reverse proxy on the same host and terminates TLS - Kestrel itself
// only gets plain HTTP (ASPNETCORE_URLS=http://+:8080) and, without this middleware, would treat every
// request as HTTP, regardless of whether the browser actually talks to nginx over HTTPS.
// Security fix: instead of clearing Known* entirely (= trusting X-Forwarded-For/-Proto from ANY
// sender), restricted to private/RFC1918 ranges. Reason: docker-compose.yml publishes
// port 8080 on ALL host interfaces (no static Docker bridge subnet can be pinned, because
// the same host runs several independent compose stacks alongside a shared
// nginx-proxy-manager instance) - anyone reaching the port directly instead of via nginx could
// otherwise forge X-Forwarded-For arbitrarily and thereby defeat the IP-based rate limiter above.
// These private ranges cover both Docker bridge subnets and the LAN through which
// nginx actually connects (neither is statically predictable) - an attacker would have to come
// from one of these ranges themselves to bypass the restriction. The gap could only be
// fully closed by NOT publishing port 8080 on all host interfaces anymore - but that
// would affect the shared nginx-proxy-manager infrastructure for other services on
// the same host and is therefore deliberately not part of this fix.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
foreach (var (address, prefixLength) in new (string Address, int PrefixLength)[]
{
    ("127.0.0.0", 8),      // Loopback
    ("10.0.0.0", 8),       // RFC1918
    ("172.16.0.0", 12),    // RFC1918 (covers Docker's default bridge range)
    ("192.168.0.0", 16),   // RFC1918
})
{
    forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(address), prefixLength));
}
app.UseForwardedHeaders(forwardedHeadersOptions);

// Scalability branch: shows WHICH instance answered a response - only for
// observing load balancing (browser DevTools/curl -v, response header), no
// functional meaning. HOSTNAME is the pod name in Kubernetes or the container id in
// Docker Compose (both set that automatically as the container's hostname) - Environment.
// MachineName reads exactly that on Linux. Harmless enough not to bother disabling it,
// it only reveals an internal hostname, not a secret.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Served-By"] = Environment.MachineName;
    await next();
});

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Skip HTTPS redirection for Kubernetes' own liveness/readiness probes (kubelet's HTTP
// probe client sends this exact User-Agent, e.g. "kube-probe/1.36" - well-established,
// documented Kubernetes behavior, not something this app controls). Found live (2026-08-14):
// kubelet's httpGet probe (k8s/04-web.yaml/05-worker.yaml, path "/", port 8080) is genuine
// plain HTTP with no X-Forwarded-Proto header - after the HttpsPort=443 fix above, it started
// looking exactly like the direct-bypass traffic this middleware is meant to catch, got
// redirected to https://.../  :443 (nothing listens there), and every pod failed readiness
// forever. Kubernetes-internal health checks were never the threat this middleware defends
// against - only requests that could plausibly be an external client reaching Kestrel
// directly are.
app.UseWhen(
    context => !(context.Request.Headers.UserAgent.ToString().StartsWith("kube-probe", StringComparison.Ordinal)),
    branch => branch.UseHttpsRedirection());

// Security headers on EVERY response (including static assets), hence here before the
// short-circuiting static-file middlewares. On the CSP - as strict as possible without a bigger
// refactor, two remaining deliberate relaxations (audit A11a, 2026-08-26: script-src's
// 'unsafe-inline' was removed - index.html's inline JS interop block (calendar swipe, WakeLock,
// push, TTS speakText, ...) now lives in wwwroot/js/interop.js + wwwroot/js/boot-loading.js,
// loaded via <script src>, so no inline script executes anymore and the directive could be
// dropped without a nonce/hash scheme):
// - script-src 'wasm-unsafe-eval': without this the browser won't compile the Blazor .wasm modules.
//   NOT 'unsafe-eval' - the WASM-specific token is narrower (only allows compiling WebAssembly,
//   not arbitrary eval()/Function()) and is what .NET's WASM runtime actually needs.
// - style-src 'unsafe-inline' + fonts.googleapis.com: inline style attributes (index.html loader,
//   dynamic chart styles) plus the Google Fonts @import in base.css; font-src analogous for the
//   actual font files from fonts.gstatic.com. Both font hosts additionally in connect-src,
//   because the service worker proxies these requests via fetch() and is thereby bound to the CSP
//   of its own script (= this one here). Out of scope for A11a (style-src, not script-src) - Blazor
//   itself sets inline style="" attributes at runtime (e.g. @bind, dynamic chart colors), so
//   removing this would need per-element nonce/hash plumbing through Blazor's own rendering, a
//   separate, larger refactor.
// Referrer-Policy same-origin: a pure same-origin SPA, the only cross-origin requests (Google Fonts)
// don't need a referrer. frame-ancestors 'none' + X-Frame-Options DENY: no embedding use case.
// media-src 'self' data:: the "read note aloud" feature (TtsController) hands the client a WAV
// as a base64 data: URI for a plain <audio src="..."> - without this, default-src's implicit
// media-src fallback blocks playback entirely (found live via a real browser, not just a code
// read-through - Chrome's console error names the exact fallback rule being hit).
// Dev exception: ws:/wss: in connect-src, so dotnet watch's browser-refresh WebSocket isn't blocked.
var csp = "default-src 'self'; "
    + "script-src 'self' 'wasm-unsafe-eval'; "
    + "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
    + "font-src 'self' https://fonts.gstatic.com; "
    + "img-src 'self' data:; "
    + "media-src 'self' data:; "
    + "connect-src 'self' https://fonts.googleapis.com https://fonts.gstatic.com"
    + (app.Environment.IsDevelopment() ? " ws: wss:; " : "; ")
    + "base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] = csp;
    await next();
});

// Bug found live: no middleware set Cache-Control on a REAL (successful) /api/* controller
// response - only the unmatched-path 404 fallback further below got "no-store", a narrower fix
// left over from the api/system/capabilities incident (a heuristically-cached stale response
// on the native app's underlying NSURLCache/HttpClient stack, which survives reinstalls and
// isn't subject to a browser's usual cache-busting reloads). Without an explicit directive here,
// the exact same class of bug can hit ANY /api GET (confirmed live: sessions/history looked
// stale in the native app while curl against the same endpoint returned fresh data) - blanket
// no-store on every /api/* response closes this for good instead of patching one endpoint at a
// time.
// MUST override via OnStarting, not a plain post-next() assignment: CacheHelper.SetHeaders
// (SessionsController/SettingsController/CoursesController) sets its own Cache-Control ("private,
// no-cache" or "private, max-age=...") from deeper in the pipeline. A first attempt set the header
// BEFORE next() and got silently overwritten (confirmed live: verifying against api/system/version -
// which sets its own explicit no-store and was never actually at risk - looked like success while
// api/sessions/history was untouched). A second attempt moved the assignment to AFTER next(), but
// for a small buffered JSON body the controller action already flushes the response (headers
// included) while still inside next() - so by the time control returns here, HasStarted is already
// true and the assignment silently no-ops, again leaving CacheHelper's header in place (confirmed
// live via a direct authenticated request: still "private, no-cache"). OnStarting is the actual
// correct primitive for this - it fires right before headers are sent regardless of when exactly
// that happens, so it reliably runs after any Cache-Control the controller already set.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    return next();
});

// Every real _framework/ payload (assemblies, native runtime, icu data) gets a content hash baked
// into its filename by the build, e.g. "StudyLife.Client.9piulg5y7p.wasm" - a content change always
// produces a new URL, so these are safe to cache for a year. The loader/manifest files that live in
// the same folder but keep a stable name across deploys (blazor.webassembly.js, dotnet.js, the boot
// json) must NOT match this, since their content can change without the URL changing.
// UseBlazorFrameworkFiles() below serves _framework/* through its own internal StaticFileOptions,
// which unconditionally appends "Cache-Control: no-cache" and short-circuits the request whenever the
// file exists - configuring StaticFileOptions on app.UseStaticFiles() further down would never even
// see these requests. Registering our OnStarting callback here, before that middleware runs, lets ours
// fire last (OnStarting runs LIFO) and overwrite the header instead of being appended to it.
var fingerprintedAssetName = new Regex(
    @"[.-](?=[A-Za-z0-9]*[A-Za-z])(?=[A-Za-z0-9]*[0-9])[A-Za-z0-9]{6,}\.[A-Za-z0-9]+$",
    RegexOptions.Compiled);
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/_framework", out var remainder)
        && fingerprintedAssetName.IsMatch(remainder.Value ?? ""))
    {
        context.Response.OnStarting(() =>
        {
            // Guard against the SPA fallback (MapFallbackToFile) ever serving index.html for a stale/
            // unmatched hashed path - never cache anything but the actual binary payload aggressively.
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !string.Equals(context.Response.ContentType?.Split(';')[0], "text/html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            }
            return Task.CompletedTask;
        });
    }
    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();

// After the static-file middlewares (assets shouldn't have to go through the limiter at all),
// before the API gate below: this way the limiter also throttles brute-force attempts against the
// API key itself. Partition/limit rationale is at AddRateLimiter above.
app.UseRateLimiter();

// Public demo instances (DEMO_MODE=true, confirmed - see DemoModeGuard): reject every mutating
// /api request with 403 before it reaches any controller - one middleware covers all of them,
// no per-endpoint auditing. The single exception is POST /api/auth/demo-login (the demo
// auto-sign-in, AuthController.DemoLogin); notably this also blocks passkey registration/login
// POSTs, so nobody can create themselves an account on a public demo. The client's offline write
// queue already treats a server rejection as "done, drop it" (only network errors are retried),
// so blocked writes act as local-only edits that a reload resets - no queue buildup. The
// middleware is only registered at all when the flag is set: a normal deployment runs a
// byte-identical pipeline.
if (DemoModeGuard.IsEnabled(app.Configuration))
{
    app.Use(async (context, next) =>
    {
        // /api/backup is blocked ENTIRELY (any method): its GET endpoints hand out the raw
        // SQLite database (/api/backup/database) and a full JSON export - and the demo user,
        // being the first AuthUser, would pass BackupController's owner check. A raw DB
        // download would include SystemSecrets (VAPID private key) and session token hashes,
        // so this must not rely on the non-GET rule below.
        if (context.Request.Path.StartsWithSegments("/api/backup"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "backups are disabled on the demo instance" });
            return;
        }
        if (context.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && !(context.Request.Path.StartsWithSegments("/api/auth/demo-login", out var demoRemainder)
                 && string.IsNullOrEmpty(demoRemainder.Value))
            // Same reasoning as TtsController being deliberately GET: dictation is POST only
            // because file uploads need a body, but it's just as pure a transform+return
            // operation - nothing persisted, so it's exempted here instead of appearing to
            // "fail to save" on the demo the way an actual blocked note edit correctly does.
            && !(context.Request.Path.StartsWithSegments("/api/dictate", out var dictateRemainder)
                 && string.IsNullOrEmpty(dictateRemainder.Value)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "read-only demo instance - changes aren't saved" });
            return;
        }
        await next();
    });
}

// Real ASP.NET Core authentication/authorization (audit finding A3) replacing the former two
// hand-rolled inline middleware lambdas ("API gate" + "current user resolution", ~140 lines
// combined). Credential resolution (session token / API key / ICS calendar token, in that
// priority) now lives in StudyLifeAuthenticationHandler; which endpoints need which credential
// is expressed per-action via [Authorize(Policy = ...)]/[AllowAnonymous] instead of the
// path-string checks that used to live here - see StudyLifeAuthorizationPolicies for the full
// policy design (ApiAccess/SessionOnly/PublicUnlessInvalidSession) and the exemption mapping.
// MUST run in this exact order (UseAuthentication before UseAuthorization) and AFTER the demo
// write-block above / BEFORE the endpoints mapped below, exactly where the former gate sat.
app.UseAuthentication();
app.UseAuthorization();

// Apple App Site Association: enables the native in-app passkey dialog (AppleSigningInfo
// doesn't check this JSON server-side itself - iOS fetches it on first app launch independently
// of the code path, as soon as the app carries the associated-domains entitlement). Deliberately NOT
// under /api (Apple fetches it without any header/token) and placed outside the API gate -
// a completely normal static path. Without Apple:TeamId config (free signing, no paid team), 404
// instead of a useless/potentially wrong response - behavior only changes with config.
// .AllowAnonymous(): AuthorizationOptions.FallbackPolicy (ApiAccess) below applies to every
// endpoint that states no requirement of its own, so this - having none - would otherwise
// suddenly need a credential too.
var appleTeamId = builder.Configuration["Apple:TeamId"];
app.MapGet("/.well-known/apple-app-site-association", (HttpResponse response) =>
{
    if (string.IsNullOrEmpty(appleTeamId)) return Results.NotFound();
    response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        webcredentials = new { apps = new[] { $"{appleTeamId}.app.studylife.mobile" } },
    });
}).AllowAnonymous();

// Audit finding D2: the generated OpenAPI document is not sensitive (public repo, no secrets in
// route/schema metadata) and needs to be fetchable by tooling/consumer CI without a credential -
// same reasoning and same .AllowAnonymous() pattern as the Apple site-association endpoint right
// above: AuthorizationOptions.FallbackPolicy (ApiAccess) applies to every endpoint with no
// authorization metadata of its own, so without this the endpoint would 401.
app.MapOpenApi().AllowAnonymous();

app.MapRazorPages();
// The default "needs a credential unless [AllowAnonymous]/a more specific policy" requirement
// for every controller action comes from AuthorizationOptions.FallbackPolicy (ApiAccess),
// configured in StudyLifeAuthorizationPolicies - not from an endpoint convention chained here,
// see that file's comment for why. This also picks up HealthController's GET /healthz/ready and
// /healthz/live (audit finding O6) - both routed outside /api and [AllowAnonymous] there, no
// mapping needed here beyond the automatic controller discovery this call already does.
app.MapControllers();
// Unknown /api paths must NEVER fall through to the SPA fallback below: that would otherwise
// deliver 200+index.html without cache headers for endpoints that don't (yet) exist on this
// server version - and HTTP caches (especially the native app's NSURLCache, which survives
// reinstalls) were allowed to heuristically cache this HTML and keep serving it to the client
// even after a server update (actually happened with api/system/capabilities on 1.21). 404 is the
// honest answer; the more specific fallback pattern wins over MapFallbackToFile. This endpoint
// also carries no explicit policy, so it too falls under FallbackPolicy (ApiAccess) - an
// unmatched /api/* path without a valid credential must still 401 (matching the former gate,
// which ran before routing even decided a path was unmatched), not leak a 404 that would let an
// unauthenticated caller distinguish "wrong path" from "not logged in".
app.MapFallback("api/{**rest}", (HttpResponse response) =>
{
    response.Headers.CacheControl = "no-store";
    return Results.NotFound();
});
// .AllowAnonymous(): the SPA shell itself (index.html) must stay reachable by anyone, including
// a browser with no session yet - it's what LOADS the login screen in the first place. Without
// this it would fall under FallbackPolicy (ApiAccess) like every other bare endpoint above.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Audit finding O2: a non-migrating process (the k8s worker, Database:Migrate=false) never calls
// Migrate() itself - it instead polls GetPendingMigrationsAsync() until the migrating process
// (web) has caught the schema up. Polling instead of blocking on EF Core's own migration lock:
// the worker stays a pure spectator, so a stuck/failed web migration shows up here as a clear,
// actionable timeout on the worker's own logs/exit code instead of an opaque hang shared with
// whatever internal lock EF Core happens to use for the provider in play.
static async Task WaitForPendingMigrationsAsync(StudyLifeDb db)
{
    var deadline = DateTime.UtcNow.AddMinutes(5);
    var attempt = 0;
    while (true)
    {
        attempt++;
        var pending = new List<string>(await db.Database.GetPendingMigrationsAsync());
        if (pending.Count == 0)
        {
            Console.WriteLine("[migrate] No pending migrations - schema already up to date, proceeding.");
            return;
        }
        if (DateTime.UtcNow >= deadline)
        {
            throw new InvalidOperationException(
                "[migrate] Timed out after 5 minutes waiting for pending migrations to be applied by "
                + $"the web role (still pending: {string.Join(", ", pending)}). This process "
                + "(Database:Migrate=false, see k8s/05-worker.yaml) never applies schema changes itself - "
                + "either the web Deployment (k8s/04-web.yaml) hasn't started/migrated yet, or its "
                + "migration is stuck/failed. Check 'kubectl -n studylife-scale logs deploy/studylife-web' "
                + "before restarting this pod.");
        }
        Console.WriteLine(
            $"[migrate] Waiting for {pending.Count} pending migration(s) to be applied by the web role "
            + $"(attempt {attempt}): {string.Join(", ", pending)}");
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
}

// Top-level statements only create an "internal" Program class - this empty partial declaration
// makes it public, so StudyLife.Server.Tests can reference it as the WebApplicationFactory<Program>
// entry point. Purely a visibility change, no runtime behavior affected.
public partial class Program { }
