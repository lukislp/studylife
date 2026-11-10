// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// studylife-ai's ingestion reads a user's notes, courses, sessions and goals through the REST
/// API with that user's AI key, then embeds them at the AI provider. The consent therefore has to
/// gate those requests too (AiConsentAuthorizationHandler), not only the proxy endpoints and key
/// generation: an existing user without a consent record, and anyone after a provider change,
/// would otherwise keep being indexed. Only the "ai" key slot is affected.
/// </summary>
public class AiKeyConsentGateTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiKeyConsentGateTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static string ExternalFp => AiConsentTestHost.ExternalFingerprint();

    private WebApplicationFactoryHandle Host(AiUpstreamStub stub) => new(_factory, stub);

    /// <summary>The shared factory with its studylife-ai replaced by a stub.</summary>
    private sealed class WebApplicationFactoryHandle
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _overridden;

        public WebApplicationFactoryHandle(CustomWebApplicationFactory factory, AiUpstreamStub stub) =>
            _overridden = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll(typeof(AiProxyClient));
                    services.AddSingleton(AiConsentTestHost.CreateClient(stub));
                }));

        public HttpClient Session() => _overridden.CreateClient();

        public HttpClient WithKey(string apiKey)
        {
            var client = _overridden.CreateClient();
            client.DefaultRequestHeaders.Remove("X-Api-Key");
            client.DefaultRequestHeaders.Remove(AuthSessionService.TokenHeaderName);
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            return client;
        }
    }

    private static async Task<string> GenerateKeyAsync(HttpClient session, string slotPath)
    {
        var response = await session.PostAsync($"/api/settings/{slotPath}/generate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("apiKey").GetString()!;
    }

    private Task SetConsentAsync(string? fingerprint) =>
        _factory.WithDbAsync(async db =>
        {
            var settings = await db.Settings.GetOrCreateAsync(db);
            settings.AiConsentGrantedAt = fingerprint is null ? null : DateTime.UtcNow;
            settings.AiConsentProviderFingerprint = fingerprint;
            await db.SaveChangesAsync();
        });

    /// <summary>An AI key obtained the normal way (consent first), with the consent then set to
    /// <paramref name="consentAfter"/>.</summary>
    private async Task<(WebApplicationFactoryHandle Host, string Key)> AiKeyAsync(AiUpstreamStub stub, string? consentAfter)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, ExternalFp);
        var host = Host(stub);
        var key = await GenerateKeyAsync(host.Session(), "ai-api-key");
        await SetConsentAsync(consentAfter);
        return (host, key);
    }

    private static async Task AssertConsentProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ai-consent-required", problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AiKey_WithoutConsent_IsRefusedWithTheConsentProblem()
    {
        // The migration case: a user with a key and no consent record.
        var (host, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: null);

        await AssertConsentProblemAsync(await host.WithKey(key).GetAsync("/api/notes"));
    }

    [Fact]
    public async Task AiKey_WithCoveringConsent_IsAllowed()
    {
        var (host, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: ExternalFp);

        Assert.Equal(HttpStatusCode.OK, (await host.WithKey(key).GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task AiKey_AfterTheProviderChanged_IsRefusedAgain()
    {
        var stub = new AiUpstreamStub();
        var (_, key) = await AiKeyAsync(stub, consentAfter: ExternalFp);
        // The operator switched the model: a new host (fresh description cache) sees the new description.
        var changed = new AiUpstreamStub
        {
            ExternalJson = AiUpstreamStub.ExternalInfoJson.Replace("gpt-4o-mini", "gpt-5", StringComparison.Ordinal),
        };

        await AssertConsentProblemAsync(await Host(changed).WithKey(key).GetAsync("/api/notes"));
    }

    [Fact]
    public async Task AiKey_WithAnUnknownProvider_NeedsConsentToo()
    {
        var (_, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: ExternalFp);
        var unknown = new AiUpstreamStub { Mode = ProviderInfoMode.NotFound };

        await AssertConsentProblemAsync(await Host(unknown).WithKey(key).GetAsync("/api/notes"));
    }

    [Fact]
    public async Task AiKey_WithALocalOnlyProvider_NeedsNoConsent()
    {
        var (_, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: null);
        var local = new AiUpstreamStub { Mode = ProviderInfoMode.Local };

        Assert.Equal(HttpStatusCode.OK, (await Host(local).WithKey(key).GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task AiKey_RefusalAlsoCoversWrites()
    {
        var (host, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: null);

        await AssertConsentProblemAsync(await host.WithKey(key).PostAsJsonAsync("/api/notes", new { title = "t", content = "c" }));
    }

    [Theory]
    [InlineData("ha-api-key")]
    [InlineData("mcp-api-key")]
    [InlineData("capture-api-key")]
    public async Task OtherKeySlots_AreUnaffected_WithoutConsent(string slotPath)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var host = Host(new AiUpstreamStub());
        var key = await GenerateKeyAsync(host.Session(), slotPath);

        Assert.Equal(HttpStatusCode.OK, (await host.WithKey(key).GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task Session_IsUnaffected_WithoutConsent()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);

        Assert.Equal(HttpStatusCode.OK, (await Host(new AiUpstreamStub()).Session().GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task AiKey_WithoutAnyAddonConfigured_IsAllowed_NothingCanLeave()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var key = await GenerateKeyAsync(_factory.CreateClient(), "ai-api-key");

        Assert.Equal(HttpStatusCode.OK, (await ApiKeyTestHelpers.CreateClientWithKey(_factory, key).GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task AiKey_OutOfScope_StillAnswersTheScopeForbidden_NotTheConsentProblem()
    {
        var (host, key) = await AiKeyAsync(new AiUpstreamStub(), consentAfter: null);

        var response = await host.WithKey(key).GetAsync("/api/settings"); // not in the "ai" scope

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
