using System.Net;

namespace StudyLife.Server.Tests;

/// <summary>
/// New pinning tests for seams introduced by audit finding A3 (real AuthenticationHandler +
/// authorization policies replacing the two hand-rolled inline middleware lambdas in
/// Program.cs). The former path-string gate ran BEFORE routing decided whether a /api path even
/// matched a controller, so an unmatched path without credentials got 401, never a 404 that
/// would let an unauthenticated caller distinguish "wrong path" from "not logged in" - the new
/// pipeline reproduces this via AuthorizationOptions.FallbackPolicy also covering the
/// "api/{**rest}" MapFallback endpoint (see Program.cs), which needed an explicit test since
/// nothing previously exercised an entirely unmatched /api path.
/// </summary>
public class AuthHandlerPipelineTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthHandlerPipelineTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UnmatchedApiPath_WithoutCredential_ReturnsUnauthorized_NotNotFound()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.GetAsync("/api/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnmatchedApiPath_WithValidSession_ReturnsNotFound()
    {
        var client = _factory.CreateClient(); // session-authenticated by default

        var response = await client.GetAsync("/api/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The SPA shell itself must stay reachable by a browser with no session yet - it's what
    /// LOADS the login screen. AuthorizationOptions.FallbackPolicy (ApiAccess) applies to any
    /// endpoint stating no requirement of its own, so MapFallbackToFile("index.html") needed an
    /// explicit .AllowAnonymous() in Program.cs; this pins that it actually works.
    /// </summary>
    [Fact]
    public async Task NonApiUnmatchedPath_WithoutCredential_ServesSpaShell()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.GetAsync("/some/client-side-route");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
