using Microsoft.AspNetCore.Hosting;

namespace StudyLife.Server.Tests;

/// <summary>
/// Program.cs fails fast (InvalidOperationException) at startup, BEFORE builder.Build(), when
/// certain provider switches are set without their mandatory companion setting - see the two
/// throws around "Cache:Provider=Redis" and "Database:Provider=Postgres" in Program.cs. Both
/// factories below inject exactly the minimal config to hit one branch (via builder.UseSetting,
/// same mechanism as AuthControllerDemoModeTests.DemoModeFactory), then boot the host on the
/// first CreateClient() call - which is where the exception actually surfaces, since it's thrown
/// by top-level statements executed while WebApplicationFactory builds/starts the entry point's
/// host, not by anything CustomWebApplicationFactory itself does.
///
/// Each factory is used and disposed within a single test (not shared via IClassFixture, unlike
/// every other test class here) - a factory whose host never finished starting has nothing usable
/// to share anyway, and xUnit's own teardown of a fixture that throws from its constructor would
/// only obscure the assertion below with a second, unrelated failure.
/// </summary>
public class ProgramStartupConfigTests
{
    private class RedisWithoutConnectionStringFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Cache:Provider", "Redis");
            // Deliberately no Cache:ConnectionString.
        }
    }

    private class PostgresWithoutConnectionStringFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Database:Provider", "Postgres");
            // Deliberately no Database:ConnectionString.
        }
    }

    /// <summary>Unwraps the exception WebApplicationFactory's host-boot machinery may wrap
    /// Program.cs's InvalidOperationException in (e.g. TargetInvocationException from invoking
    /// the generated top-level-statements Main via reflection), so the assertion below can check
    /// the actual message regardless of exactly how many layers it travels through.</summary>
    private static Exception Unwrap(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    [Fact]
    public void CacheProviderRedis_WithoutConnectionString_ThrowsAtStartup()
    {
        using var factory = new RedisWithoutConnectionStringFactory();

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var actual = Unwrap(thrown);
        Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("Cache:Provider=Redis", actual.Message);
    }

    [Fact]
    public void DatabaseProviderPostgres_WithoutConnectionString_ThrowsAtStartup()
    {
        using var factory = new PostgresWithoutConnectionStringFactory();

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var actual = Unwrap(thrown);
        Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("Database:Provider=Postgres", actual.Message);
    }
}
