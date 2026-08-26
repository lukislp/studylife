using Microsoft.AspNetCore.Mvc.Testing;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pins the security headers middleware in Program.cs (audit A11a, 2026-08-26: script-src's
/// 'unsafe-inline' was removed after index.html's inline JS interop block was extracted into
/// wwwroot/js/interop.js + wwwroot/js/boot-loading.js). This guards against a future edit
/// silently reintroducing 'unsafe-inline' on script-src, or dropping 'wasm-unsafe-eval' (which
/// would break the Blazor WASM boot - the browser can't compile the .wasm modules without it).
/// </summary>
public class SecurityHeadersTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityHeadersTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Response_HasContentSecurityPolicyHeader()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        Assert.Single(values!);
    }

    [Fact]
    public async Task ScriptSrc_DoesNotContainUnsafeInline()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");
        var scriptSrc = GetDirective(response, "script-src");

        Assert.NotNull(scriptSrc);
        Assert.DoesNotContain("'unsafe-inline'", scriptSrc);
    }

    [Fact]
    public async Task ScriptSrc_AllowsWasmUnsafeEval_ButNotPlainUnsafeEval()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");
        var scriptSrc = GetDirective(response, "script-src");

        Assert.NotNull(scriptSrc);
        Assert.Contains("'wasm-unsafe-eval'", scriptSrc);
        // 'unsafe-eval' is a strict superset of 'wasm-unsafe-eval' - assert the plain token isn't
        // present as its own directive value (not just a substring hit inside 'wasm-unsafe-eval').
        var tokens = scriptSrc!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("'unsafe-eval'", tokens);
    }

    [Fact]
    public async Task Response_HasOtherExpectedSecurityHeaders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    private static string? GetDirective(HttpResponseMessage response, string directiveName)
    {
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var directive = csp.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(d => d.StartsWith(directiveName + " ", StringComparison.Ordinal));
        return directive?[(directiveName.Length + 1)..];
    }
}
