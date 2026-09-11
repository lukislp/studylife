using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-09-11 audit: outside Development the pipeline used UseExceptionHandler("/Error"), a
/// Razor page this project never had - the re-executed request fell through to the SPA
/// fallback and API callers got index.html with a 500. Now AddProblemDetails +
/// UseExceptionHandler() answer application/problem+json. The test host runs as Development
/// (developer exception page), so this factory flips to Production and adds a throwing
/// controller that only exists in this test assembly.
/// </summary>
[ApiController]
[Route("api/test-throw")]
[AllowAnonymous]
public class ThrowingTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => throw new InvalidOperationException("deliberate test failure");
}

public class ProductionExceptionHandlerFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ThrowingTestController).Assembly));
    }
}

public class ProblemDetailsExceptionHandlerTests : IClassFixture<ProductionExceptionHandlerFactory>
{
    private readonly HttpClient _client;

    public ProblemDetailsExceptionHandlerTests(ProductionExceptionHandlerFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task UnhandledException_InProduction_AnswersProblemJson_NotTheSpaShell()
    {
        var response = await _client.GetAsync("/api/test-throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":500", body);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliberate test failure", body); // no exception details leak to the client
    }
}
