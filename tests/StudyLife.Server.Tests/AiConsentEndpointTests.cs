// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// HTTP surface of the AI consent: the consent endpoints and every enforced path that sends
/// user data to studylife-ai (the three proxy endpoints and AI key generation). The rule is
/// AiConsentService's (AiConsentServiceTests); these tests pin the mapping to HTTP - the 403
/// problem the client reacts to, the 400/409 of the grant, and what reaches the add-on.
/// </summary>
public class AiConsentEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiConsentEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static string ExternalFp => AiConsentTestHost.ExternalFingerprint();

    // ── status ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ReportsTheProviderAndThatConsentIsRequired()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, consentFingerprint: null);
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var response = await client.GetAsync("/api/ai/consent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var status = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
        Assert.True(status!.AiAvailable);
        Assert.True(status.ConsentRequired);
        Assert.False(status.Granted);
        Assert.True(status.HasKey);
        Assert.True(status.NeedsDecision);
        Assert.Equal(ExternalFp, status.Fingerprint);
        Assert.Contains(status.Provider!.Roles, r => r is { Role: "chat", Provider: "OpenAI", Model: "gpt-4o-mini", Local: false });
    }

    [Fact]
    public async Task Capabilities_CarryAiAvailabilityAndTheProviderDescription()
    {
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var caps = await client.GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");

        Assert.True(caps!.AiAvailable);
        Assert.True(caps.AiProvider!.AnyExternal);
        Assert.Equal(2, caps.AiProvider.Roles.Count);
    }

    [Fact]
    public async Task Capabilities_WithAnOlderAddon_HaveNoProviderDescription()
    {
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub { Mode = ProviderInfoMode.NotFound });

        var caps = await client.GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");

        Assert.True(caps!.AiAvailable);
        Assert.Null(caps.AiProvider);
    }

    [Fact]
    public async Task Capabilities_WithoutAnAddon_ReportAiUnavailable()
    {
        var caps = await _factory.CreateClient().GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");

        Assert.False(caps!.AiAvailable);
        Assert.Null(caps.AiProvider);
    }

    [Fact]
    public async Task SetupOverview_CapabilitiesCarryTheAiFieldsToo()
    {
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var overview = await client.GetFromJsonAsync<SetupOverviewDto>("/api/setup/overview");

        Assert.True(overview!.Capabilities!.AiAvailable);
        Assert.NotNull(overview.Capabilities.AiProvider);
    }

    // ── grant ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Grant_RecordsTheConsent_AndTheStatusThenCovers()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var response = await client.PostAsJsonAsync("/api/ai/consent/grant", new AiConsentGrantRequestDto { Fingerprint = ExternalFp });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
        Assert.True(status!.Granted);
        Assert.NotNull(status.GrantedAt);
        Assert.True((await client.GetFromJsonAsync<AiConsentStatusDto>("/api/ai/consent"))!.Granted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-fingerprint")]
    public async Task Grant_WithAMalformedFingerprint_Returns400(string fingerprint)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var response = await client.PostAsJsonAsync("/api/ai/consent/grant", new AiConsentGrantRequestDto { Fingerprint = fingerprint });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Grant_ForAProviderThatChangedSinceTheDialog_Returns409WithTheCurrentStatus()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var response = await client.PostAsJsonAsync("/api/ai/consent/grant", new AiConsentGrantRequestDto { Fingerprint = new string('c', 64) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
        Assert.Equal(ExternalFp, current!.Fingerprint);
        Assert.False(current.Granted);
    }

    [Fact]
    public async Task Grant_WithoutAnAddon_Returns400()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/ai/consent/grant", new AiConsentGrantRequestDto { Fingerprint = new string('a', 64) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/ai/consent")]
    [InlineData("POST", "/api/ai/consent/grant")]
    [InlineData("POST", "/api/ai/consent/withdraw")]
    public async Task ConsentEndpoints_WithoutASession_ReturnUnauthorized(string method, string path)
    {
        // An API key alone must not be able to grant (or withdraw) a consent.
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new AiConsentGrantRequestDto { Fingerprint = new string('a', 64) }) : null,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── withdraw ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_ClearsTheConsent_AndRevokesTheKeyThroughTheRevokePath()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, ExternalFp);
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsync("/api/ai/consent/withdraw", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (consentAt, fingerprint, keyHash) = await _factory.WithDbAsync(async db =>
        {
            var settings = await db.Settings.AsNoTracking().FirstAsync();
            var user = await db.AuthUsers.AsNoTracking().FirstAsync(u => u.Id == 1);
            return (settings.AiConsentGrantedAt, settings.AiConsentProviderFingerprint, user.AiApiKeyHash);
        });
        Assert.Null(consentAt);
        Assert.Null(fingerprint);
        Assert.Null(keyHash);
        Assert.Equal(1, stub.Count("/internal/revoke-key")); // studylife-ai purges the user's data
        Assert.False((await client.GetFromJsonAsync<AiConsentStatusDto>("/api/ai/consent"))!.Granted);
    }

    [Fact]
    public async Task Disable_StillWorks_AndKeepsTheConsent()
    {
        // The existing "disable" action only revokes the key; the consent stays so enabling
        // again does not ask a second time.
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, ExternalFp);
        var client = AiConsentTestHost.CreateHttpClient(_factory, new AiUpstreamStub());

        var response = await client.PostAsync("/api/settings/ai-api-key/revoke", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True((await client.GetFromJsonAsync<AiConsentStatusDto>("/api/ai/consent"))!.Granted);
    }

    // ── enforcement: proxy ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/ai/chat", "/chat")]
    [InlineData("/api/ai/agent", "/agent")]
    [InlineData("/api/ai/agent/confirm", "/agent/confirm")]
    public async Task Proxy_WithoutConsent_Returns403Problem_AndNothingReachesTheAddon(string path, string upstreamPath)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, consentFingerprint: null);
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ai-consent-required", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, stub.Count(upstreamPath));
    }

    [Theory]
    [InlineData("/api/ai/chat", "/chat")]
    [InlineData("/api/ai/agent", "/agent")]
    [InlineData("/api/ai/agent/confirm", "/agent/confirm")]
    public async Task Proxy_WithAConsentForAnotherProvider_IsRefusedUntilAskedAgain(string path, string upstreamPath)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, new string('d', 64));
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, stub.Count(upstreamPath));
    }

    [Theory]
    [InlineData("/api/ai/chat", "/chat")]
    [InlineData("/api/ai/agent", "/agent")]
    [InlineData("/api/ai/agent/confirm", "/agent/confirm")]
    public async Task Proxy_WithCoveringConsent_ForwardsToTheAddon(string path, string upstreamPath)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, ExternalFp);
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.Count(upstreamPath));
    }

    [Fact]
    public async Task Proxy_WithLocalOnlyProvider_NeedsNoConsent()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.Local };
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsJsonAsync("/api/ai/chat", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.Count("/chat"));
    }

    [Fact]
    public async Task Proxy_WithUnknownProvider_RefusesWithoutConsent()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.NotFound };
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsJsonAsync("/api/ai/chat", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, stub.Count("/chat"));
    }

    // ── enforcement: key generation (where ingestion starts) ─────────────────────────────────

    [Fact]
    public async Task GenerateKey_WithoutConsent_Returns403Problem_AndRegistersNothing()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsync("/api/settings/ai-api-key/generate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ai-consent-required", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, stub.Count("/internal/register-key")); // ingestion never starts
        var hasKey = await _factory.WithDbAsync(async db =>
            (await db.AuthUsers.AsNoTracking().FirstAsync(u => u.Id == 1)).AiApiKeyHash != null);
        Assert.False(hasKey);
    }

    [Fact]
    public async Task GenerateKey_AfterGrant_RegistersTheKey()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub();
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);
        await client.PostAsJsonAsync("/api/ai/consent/grant", new AiConsentGrantRequestDto { Fingerprint = ExternalFp });

        var response = await client.PostAsync("/api/settings/ai-api-key/generate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.Count("/internal/register-key"));
    }

    [Fact]
    public async Task GenerateKey_WithLocalOnlyProvider_NeedsNoConsent()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.Local };
        var client = AiConsentTestHost.CreateHttpClient(_factory, stub);

        var response = await client.PostAsync("/api/settings/ai-api-key/generate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.Count("/internal/register-key"));
    }

    [Fact]
    public async Task GenerateKey_WithoutAnAddon_StillWorks_NothingCanLeaveTheServer()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);

        var response = await _factory.CreateClient().PostAsync("/api/settings/ai-api-key/generate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
