using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

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

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
    db.Database.Migrate();
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
    // above (SystemSecrets stay untouched), and never reachable without DEMO_MODE=true.
    if (string.Equals(builder.Configuration["DEMO_MODE"], "true", StringComparison.OrdinalIgnoreCase))
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

app.UseHttpsRedirection();

// Security headers on EVERY response (including static assets), hence here before the
// short-circuiting static-file middlewares. On the CSP - as strict as possible without a bigger
// refactor, three deliberate relaxations:
// - script-src 'unsafe-inline': index.html contains a large inline JS interop block
//   (calendar swipe, WakeLock, push, ...). Nonce/hash isn't feasible for a statically served
//   WASM site without extracting it into an external file - a separate refactor, out of scope here.
// - script-src 'wasm-unsafe-eval': without this the browser won't compile the Blazor .wasm modules.
// - style-src 'unsafe-inline' + fonts.googleapis.com: inline style attributes (index.html loader,
//   dynamic chart styles) plus the Google Fonts @import in base.css; font-src analogous for the
//   actual font files from fonts.gstatic.com. Both font hosts additionally in connect-src,
//   because the service worker proxies these requests via fetch() and is thereby bound to the CSP
//   of its own script (= this one here).
// Referrer-Policy same-origin: a pure same-origin SPA, the only cross-origin requests (Google Fonts)
// don't need a referrer. frame-ancestors 'none' + X-Frame-Options DENY: no embedding use case.
// Dev exception: ws:/wss: in connect-src, so dotnet watch's browser-refresh WebSocket isn't blocked.
var csp = "default-src 'self'; "
    + "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; "
    + "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
    + "font-src 'self' https://fonts.gstatic.com; "
    + "img-src 'self' data:; "
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

// Public demo instances (DEMO_MODE=true): reject every mutating /api request with 403 before
// it reaches any controller - one middleware covers all of them, no per-endpoint auditing.
// The single exception is POST /api/auth/demo-login (the demo auto-sign-in,
// AuthController.DemoLogin); notably this also blocks passkey registration/login POSTs, so
// nobody can create themselves an account on a public demo. The client's offline write queue
// already treats a server rejection as "done, drop it" (only network errors are retried), so
// blocked writes act as local-only edits that a reload resets - no queue buildup. The
// middleware is only registered at all when the flag is set: a normal deployment runs a
// byte-identical pipeline.
if (string.Equals(app.Configuration["DEMO_MODE"], "true", StringComparison.OrdinalIgnoreCase))
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
                 && string.IsNullOrEmpty(demoRemainder.Value)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "read-only demo instance - changes aren't saved" });
            return;
        }
        await next();
    });
}

// API gate, always active (phase 3): every /api request needs EITHER a valid
// passkey session token (X-Session-Token, phase 2 - the normal path of the browser client)
// OR a per-user API key (X-Api-Key header or ?apiKey= query string for URL-only
// consumers) - the latter is a long-lived key, NEVER automatically rotated, for non-interactive
// integrations. Three independent slots exist per user: AuthUserEntity.ApiKeyHash (Home
// Assistant), AuthUserEntity.AiApiKeyHash (studylife-ai), and AuthUserEntity.McpApiKeyHash
// (studylife-mcp), all generated via the setup page - separate slots so one integration's key
// leaking/rotating never affects the others.
// The former global, monthly-rotating key (ApiKeyProvider) and its
// unauthenticated bootstrap-key endpoint have been completely removed - the browser client
// authenticates exclusively via its session, an API key always identifies
// exactly ONE user (no more "first user" fallback for key requests).
// Exceptions below unchanged: ICS feed (own permanent token), public
// progress-share link, /api/auth (login/registration).
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        // First exception: the ICS calendar feed needs its own permanent token
        // (AuthUserEntity.CalendarToken), because subscribing calendar apps can neither set headers
        // nor go through a login. Deliberately limited to GET + exact path, so
        // no other /api/sessions/... endpoint is accidentally exempted too - and deliberately
        // a separate query parameter name (calendarToken instead of apiKey), so the two
        // secret spaces aren't interchangeable. Unlike the former global
        // CalendarTokenProvider, the token here resolves the OWNING user (like
        // the per-user API key below) - otherwise every calendar token would show the same
        // (the first-registered) user, regardless of who it actually belongs to.
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/sessions/ics", out var icsRemainder)
            && string.IsNullOrEmpty(icsRemainder.Value))
        {
            var calendarToken = context.Request.Query["calendarToken"].FirstOrDefault();
            if (string.IsNullOrEmpty(calendarToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var icsDb = context.RequestServices.GetRequiredService<StudyLifeDb>();
            var calendarOwner = await icsDb.AuthUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.CalendarToken == calendarToken);
            if (calendarOwner is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items[CurrentUserAccessor.HttpContextItemKey] = calendarOwner.Id;
            await next();
            return;
        }

        // Second exception: the public read-only progress link (ProgressController),
        // for sharing with parents/mentor/study advisor without an API key. Unlike the ICS token
        // (permanent in-memory singleton, CalendarTokenProvider), the progress-share token
        // lives per settings row in the DB (UserSettingsEntity.ProgressShareToken) and can be
        // toggled on/off via the setup page - the check therefore needs a DB access and deliberately
        // does NOT happen here in the middleware-singleton style, but in the controller itself (ProgressController.
        // GetShared returns 404 instead of 401 for an invalid/missing/disabled token, so a
        // scanner doesn't even learn whether the path exists). Deliberately limited to GET + a non-empty
        // remaining path, so no other /api/progress/... endpoint is accidentally exempted too.
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/progress/shared", out var progressRemainder)
            && !string.IsNullOrEmpty(progressRemainder.Value) && progressRemainder.Value != "/")
        {
            await next();
            return;
        }

        // Third exception (phase 2, passkey login): /api/auth must be reachable without any
        // existing secret - registration/login are exactly the path through which one gets a
        // secret in the first place. The session-required subpaths (logout,
        // device list, additional passkey) check within AuthController itself via the
        // AuthSessionService.SessionItemKey item set by the resolution middleware below.
        if (context.Request.Path.StartsWithSegments("/api/auth"))
        {
            await next();
            return;
        }

        // Fourth exception: /api/system/version only shows the build version number (setup page),
        // no user context, no sensitive data - needs no session/no API key. Deliberately
        // limited to GET + exact path like the ICS/progress exceptions above.
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/system/version", out var versionRemainder)
            && string.IsNullOrEmpty(versionRemainder.Value))
        {
            await next();
            return;
        }

        // Priority order (phase 3): session token has highest priority - if one is present,
        // it EXCLUSIVELY applies, and an invalid/expired token leads to 401 even alongside a
        // valid API key (rationale at the resolution middleware below). The
        // validation extends the session with a sliding window at the same time and stores the user +
        // session id in Items, so the resolution middleware doesn't validate twice.
        var gateToken = context.Request.Headers[AuthSessionService.TokenHeaderName].FirstOrDefault();
        if (!string.IsNullOrEmpty(gateToken))
        {
            var gateDb = context.RequestServices.GetRequiredService<StudyLifeDb>();
            var gateSession = await AuthSessionService.ValidateAndRefreshAsync(gateDb, gateToken, DateTime.UtcNow);
            if (gateSession is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items[CurrentUserAccessor.HttpContextItemKey] = gateSession.AuthUserId;
            context.Items[AuthSessionService.SessionItemKey] = gateSession.Id;
        }
        else
        {
            // Without a session token: per-user API key. The hash of the submitted plaintext
            // key is matched against AuthUsers.ApiKeyHash (Home Assistant & co), OR
            // AuthUsers.AiApiKeyHash (studylife-ai), OR AuthUsers.McpApiKeyHash
            // (studylife-mcp) - three separate slots (see AuthUserEntity.AiApiKeyHash /
            // McpApiKeyHash) so revoking/rotating one integration's key can never affect the
            // others, but any one of them authenticates AND identifies the user in one step
            // here, there is no more "first user" fallback for key requests.
            // Deliberately NO SessionItemKey item: session-required endpoints (passkey
            // management, ha-api-key/*, ai-api-key/*, mcp-api-key/*) remain off-limits for pure
            // key consumers.
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.Request.Query["apiKey"].FirstOrDefault();
            if (string.IsNullOrEmpty(provided))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var keyHash = AuthSessionService.HashToken(provided);
            var keyDb = context.RequestServices.GetRequiredService<StudyLifeDb>();
            var keyOwner = await keyDb.AuthUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.ApiKeyHash == keyHash || u.AiApiKeyHash == keyHash || u.McpApiKeyHash == keyHash);
            if (keyOwner is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items[CurrentUserAccessor.HttpContextItemKey] = keyOwner.Id;
        }
    }
    await next();
});

// "Current user" resolution, deliberately AFTER the API gate above. Since phase 3, the gate already
// sets the items for session, API key, AND (since the security fix) calendar-token requests
// itself - here, only the public progress-share link and /api/auth arrive without a resolved
// user. Priority order:
// 1. If an X-Session-Token came along (possible even on the exception paths), its user
//    EXCLUSIVELY applies - including sliding extension (ExpiresAt = now + 90 days, hard
//    capped at HardExpiresAt). An INVALID/expired token leads to 401: silently falling back
//    to the fallback user would show the wrong user's data, and the
//    explicit 401 is exactly the signal that makes the client discard its token and redirect to
//    login. Exception /api/auth: there the request continues unauthenticated,
//    otherwise one could never log in again with an expired token in localStorage.
// 2. Without any header (anonymous progress-share request): phase-1 fallback to the first
//    AuthUserEntity - harmless, because ProgressController.GetShared resolves the actual token
//    owner itself via IgnoreQueryFilters()+BeginBackgroundScope and overwrites this
//    fallback for its own queries in the process. /api/auth deliberately gets NO
//    fallback: the session-required auth endpoints must never rely on a
//    fallback resolution.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var isAuthPath = context.Request.Path.StartsWithSegments("/api/auth");
        if (!context.Items.ContainsKey(CurrentUserAccessor.HttpContextItemKey))
        {
            var db = context.RequestServices.GetRequiredService<StudyLifeDb>();
            var token = context.Request.Headers[AuthSessionService.TokenHeaderName].FirstOrDefault();
            if (!string.IsNullOrEmpty(token))
            {
                var session = await AuthSessionService.ValidateAndRefreshAsync(db, token, DateTime.UtcNow);
                if (session is not null)
                {
                    context.Items[CurrentUserAccessor.HttpContextItemKey] = session.AuthUserId;
                    context.Items[AuthSessionService.SessionItemKey] = session.Id;
                }
                else if (!isAuthPath)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            if (!isAuthPath && !context.Items.ContainsKey(CurrentUserAccessor.HttpContextItemKey))
            {
                var authUser = await db.AuthUsers.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync();
                if (authUser is not null)
                    context.Items[CurrentUserAccessor.HttpContextItemKey] = authUser.Id;
            }
        }
    }
    await next();
});

// Apple App Site Association: enables the native in-app passkey dialog (AppleSigningInfo
// doesn't check this JSON server-side itself - iOS fetches it on first app launch independently
// of the code path, as soon as the app carries the associated-domains entitlement). Deliberately NOT
// under /api (Apple fetches it without any header/token) and placed outside the API gate -
// a completely normal static path. Without Apple:TeamId config (free signing, no paid team), 404
// instead of a useless/potentially wrong response - behavior only changes with config.
var appleTeamId = builder.Configuration["Apple:TeamId"];
app.MapGet("/.well-known/apple-app-site-association", (HttpResponse response) =>
{
    if (string.IsNullOrEmpty(appleTeamId)) return Results.NotFound();
    response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        webcredentials = new { apps = new[] { $"{appleTeamId}.app.studylife.mobile" } },
    });
});

app.MapRazorPages();
app.MapControllers();
// Unknown /api paths must NEVER fall through to the SPA fallback below: that would otherwise
// deliver 200+index.html without cache headers for endpoints that don't (yet) exist on this
// server version - and HTTP caches (especially the native app's NSURLCache, which survives
// reinstalls) were allowed to heuristically cache this HTML and keep serving it to the client
// even after a server update (actually happened with api/system/capabilities on 1.21). 404 is the
// honest answer; the more specific fallback pattern wins over MapFallbackToFile.
app.MapFallback("api/{**rest}", (HttpResponse response) =>
{
    response.Headers.CacheControl = "no-store";
    return Results.NotFound();
});
app.MapFallbackToFile("index.html");

app.Run();

// Top-level statements only create an "internal" Program class - this empty partial declaration
// makes it public, so StudyLife.Server.Tests can reference it as the WebApplicationFactory<Program>
// entry point. Purely a visibility change, no runtime behavior affected.
public partial class Program { }
