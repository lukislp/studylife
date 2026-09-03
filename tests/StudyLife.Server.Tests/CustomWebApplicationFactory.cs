using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// WebApplicationFactory for integration tests against the real HTTP stack (Program.cs runs
/// completely through, including middleware, Migrate(), and PRAGMAs), but with three test
/// rewirings:
///
/// 1. DB isolation: the DbContextOptions from Program.cs (app_data/studylife.db) are removed
///    and AddDbContextPool is rerouted to a temp file unique per factory instance.
///    Program.cs's own Migrate() block runs at host startup (triggered by the first
///    CreateClient()) against exactly this temp DB - so the schema is guaranteed to exist before
///    the first request goes out, without needing any migration code here.
/// 2. BackgroundTaskService: the IHostedService descriptor is removed, so the 30s poller
///    neither sends push notifications nor writes to the test DB during tests.
/// 3. DatabaseBackupService: the singleton instance constructed in Program.cs with real paths
///    is replaced with one pointing at temp DB + temp backup directory, so backup tests
///    never write into the repo/app_data.
///
/// Usage: one instance per test class via IClassFixture&lt;CustomWebApplicationFactory&gt; -
/// tests within a class share DB state (xUnit runs them sequentially, but in
/// arbitrary order), different test classes get their own factories/DBs and
/// run safely in parallel. Tests that need a pristine DB therefore belong in
/// their own class without mutating sibling tests.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Path of this factory instance's temp SQLite file (for isolation asserts).</summary>
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"studylife-test-{Guid.NewGuid():N}.db");

    /// <summary>Temp content root only for DatabaseBackupService (app_data/backups lands under this).</summary>
    public string BackupContentRoot { get; } = Path.Combine(Path.GetTempPath(), $"studylife-test-root-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Registration gate (audit finding A10): production defaults to "invite" when unset, but
        // dozens of pre-existing tests across this suite register a SECOND user (the "family
        // signup" scenario, e.g. OwnershipRegistrationTests/AccountInfoTests/
        // BackupControllerOwnerRestrictionTests/PasskeyApprovalTests/*CacheIsolationTests/
        // *MultiUserTests) via PasskeyHttp.RegisterAsync without ever passing an invite token -
        // exactly what "invite" mode would now reject. Defaulting the shared test factory to
        // "open" preserves that established, widely-relied-upon assumption unchanged for every
        // test that isn't specifically about the gate itself. RegistrationGateInviteModeTests/
        // RegistrationGateClosedModeTests override this explicitly back to "invite"/"closed" (same
        // per-factory-subclass override pattern as DEMO_MODE in AuthControllerEdgeTests.cs) -
        // the actual "unset defaults to invite" production behavior is pinned separately by the
        // pure unit test RegistrationModeConfigTests.Unset_DefaultsToInvite (no host needed there).
        builder.UseSetting("Registration:Mode", "open");
        // The mcp audience's consent flow only accepts loopback or explicitly configured
        // callbacks (ConsentRedirectPolicy) - the https callback the mcp flow tests use has to be
        // on that list, exactly like a real HTTP-mode studylife-mcp deployment configures its own.
        builder.UseSetting("Consent:AllowedRedirectUris:mcp:0", "https://mcp.example.com/auth/studylife/callback");

        // Runs AFTER the registrations from Program.cs, but BEFORE the host starts - rerouting the
        // descriptors therefore takes effect before Program.cs's Migrate() block touches the DB for the first time.
        builder.ConfigureServices(services =>
        {
            // Reroute the DbContext registration from Program.cs (AddDbContext, without pooling
            // since the multi-tenant rework, see the comment there) to the temp DB - after the
            // RemoveAll, the new options descriptor wins.
            services.RemoveAll(typeof(DbContextOptions<StudyLifeDb>));
            services.RemoveAll(typeof(DbContextOptions));
            services.AddDbContext<StudyLifeDb>(opt => opt.UseSqlite($"Data Source={DbPath}"));

            // Multi-tenant foundation: tests frequently access the DB directly via
            // factory.Services.CreateScope() (without an HTTP request and without a background
            // scope) - there, ICurrentUserAccessor would otherwise return 0 and the global query
            // filters would hide everything. The fallback points at the one AuthUserEntity that
            // the AddMultiTenantAuthUserFoundation migration seeds into every freshly migrated
            // (temp) DB - deterministically Id 1, because the table is always empty when seeding.
            // Being static and thus shared by all factories running in parallel is correct here,
            // because every temp DB has the same seed state.
            CurrentUserAccessor.AmbientFallbackAuthUserId = 1;

            var backgroundTask = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(BackgroundTaskService));
            if (backgroundTask is not null) services.Remove(backgroundTask);

            services.RemoveAll(typeof(DatabaseBackupService));
            Directory.CreateDirectory(BackupContentRoot);
            services.AddSingleton(new DatabaseBackupService(DbPath, BackupContentRoot));

            // Same as with DatabaseBackupService: reroute the instance constructed in Program.cs
            // with the real app_data path, otherwise POST /api/backup/restore in tests would put
            // the staging file next to the REAL live DB. The staging path is derived from the DB
            // file name (DatabaseRestoreService.GetStagingPath) and therefore unique per factory.
            services.RemoveAll(typeof(DatabaseRestoreService));
            services.AddSingleton(new DatabaseRestoreService(DbPath));

            // SystemSecretsService (VAPID keys/setup code) needs NO rerouting anymore - since
            // the scalability branch it's DB-backed instead of file-based, so it automatically
            // lands in this factory instance's temp DB (already rerouted above), with no
            // dedicated isolation directory like SetupSecretService used to need.
        });
    }

    /// <summary>Plaintext session token of the test user auto-logged-in via ConfigureClient
    /// (AuthUserId 1, seeded by the AddMultiTenantAuthUserFoundation migration) - for tests
    /// that want to specifically manipulate/omit the token (see ApiKeyTestHelpers below).</summary>
    public string SessionToken { get; private set; } = "";

    // Runs on every CreateClient() AFTER host startup: logs in the seeded test user via
    // session token (phase 3: replaces the former static X-Api-Key default header),
    // so existing controller tests don't notice anything from the always-active /api gate. The
    // token is only issued once and reused across multiple CreateClient() calls.
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        // Simulates the nginx/NPM hop every real request actually goes through (Program.cs's
        // UseForwardedHeaders trusts this from loopback, which is what TestServer's client
        // connects as) - without it, every test request looks like plain, unproxied HTTP
        // straight to Kestrel, which since the HttpsRedirectionOptions.HttpsPort fix now
        // actually redirects (as intended - that's the direct-bypass case the forwarded-headers
        // restriction exists for) instead of just logging an ignored warning. Request.Scheme
        // feeds Fido2's dynamic Origins (AuthController.CreateFido2) - PasskeyHttp.Origin below
        // matches this.
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        if (string.IsNullOrEmpty(SessionToken))
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            SessionToken = AuthSessionService.IssueSession(db, authUserId: 1, DateTime.UtcNow);
            db.SaveChanges();
        }
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, SessionToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        // Microsoft.Data.Sqlite pools connections per connection string and otherwise keeps the
        // file locked on Windows - clear the pool first, then delete. Deliberately clearing ONLY
        // THIS factory's pool (ClearPool) instead of the former global ClearAllPools: factories
        // run in parallel (one per test class), and the global clear on a class's dispose
        // occasionally yanked pooled connections out from under neighboring factories' running
        // queries - observed as sporadic SafeHandle/ObjectDisposed errors in unrelated tests.
        using (var poolProbe = new SqliteConnection($"Data Source={DbPath}"))
            SqliteConnection.ClearPool(poolProbe);
        var stagingPath = DatabaseRestoreService.GetStagingPath(DbPath);
        foreach (var file in new[] { DbPath, DbPath + "-wal", DbPath + "-shm",
                                     stagingPath, stagingPath + ".rejected" })
        {
            try { File.Delete(file); } catch (IOException) { /* cleanup is best effort */ }
        }
        try { Directory.Delete(BackupContentRoot, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}

/// <summary>
/// Shorthand for the block that recurs across BackgroundTaskService*Tests:
/// "using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.
/// GetRequiredService&lt;StudyLifeDb&gt;();" - saves the manual scope handling at each
/// individual call site, without touching CustomWebApplicationFactory itself.
/// </summary>
public static class CustomWebApplicationFactoryDbExtensions
{
    public static async Task<T> WithDbAsync<T>(this CustomWebApplicationFactory factory, Func<StudyLifeDb, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }

    public static async Task WithDbAsync(this CustomWebApplicationFactory factory, Func<StudyLifeDb, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }
}

/// <summary>
/// Client creation with explicit (or deliberately missing) auth state. ConfigureClient attaches
/// a valid session token for the seeded test user to every request by default -
/// tests that want to check a DIFFERENT/missing api key or need to act truly anonymously (no
/// token, no key, e.g. progress-share/ICS tests) therefore remove both default
/// headers before setting exactly one header on purpose. Formerly homed in
/// ApiKeyProviderTests.cs, moved here when that file was removed (phase 3: no more global
/// ApiKeyProvider), because several other test files (ProgressControllerTests, SessionsControllerTests,
/// SystemControllerRegenerateTests) still need this helper.
/// </summary>
internal static class ApiKeyTestHelpers
{
    public static HttpClient CreateClientWithKey(CustomWebApplicationFactory factory, string? apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Remove(AuthSessionService.TokenHeaderName);
        if (apiKey != null) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}
