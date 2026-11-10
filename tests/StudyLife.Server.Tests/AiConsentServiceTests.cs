// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The consent rule itself (AiConsentService) and the provider-description cache behind it
/// (AiProxyClient.GetProviderInfoAsync): when consent is required, what a consent covers, and
/// that a missing description never lets a request through. HTTP mapping is covered separately
/// in AiConsentEndpointTests; the worker path in BackgroundTaskServiceCaptureEnrichmentTests.
/// </summary>
public class AiConsentServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiConsentServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private Task<T> WithServiceAsync<T>(AiUpstreamStub stub, Func<AiConsentService, Task<T>> action,
        TimeProvider? time = null, bool demo = false) =>
        _factory.WithDbAsync(db => action(new AiConsentService(db, AiConsentTestHost.CreateClient(stub, time), demo)));

    // ── required / not required ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExternalProvider_RequiresConsent_AndRefusesWithoutIt()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.External };

        var (status, gate) = await WithServiceAsync(stub, async s => (await s.GetStatusAsync(1, default), await s.EnsureGrantedAsync(1, default)));

        Assert.True(status.AiAvailable);
        Assert.True(status.ConsentRequired);
        Assert.False(status.Granted);
        Assert.True(status.ProviderKnown);
        Assert.Equal(ServiceOutcome.Forbidden, gate.Outcome);
        Assert.Equal(AiConsentService.ConsentRequiredProblem, gate.Error);
    }

    [Fact]
    public async Task LocalOnlyProvider_NeedsNoConsent()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.Local };

        var (status, gate) = await WithServiceAsync(stub, async s => (await s.GetStatusAsync(1, default), await s.EnsureGrantedAsync(1, default)));

        Assert.False(status.ConsentRequired);
        Assert.False(status.NeedsDecision);
        Assert.Equal(ServiceOutcome.Success, gate.Outcome);
    }

    [Theory]
    [InlineData(ProviderInfoMode.NotFound)]
    [InlineData(ProviderInfoMode.Unreachable)]
    public async Task UnknownProvider_IsTreatedAsExternal_NeverSkipsConsent(ProviderInfoMode mode)
    {
        // An older studylife-ai (404) or an unreachable one gives no description: that must not
        // be read as "nothing to ask".
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub { Mode = mode };

        var (status, gate) = await WithServiceAsync(stub, async s => (await s.GetStatusAsync(1, default), await s.EnsureGrantedAsync(1, default)));

        Assert.True(status.ConsentRequired);
        Assert.False(status.ProviderKnown);
        Assert.Null(status.Provider);
        Assert.Equal(AiConsentTestHost.UnknownFingerprint(), status.Fingerprint);
        Assert.Equal(ServiceOutcome.Forbidden, gate.Outcome);
    }

    [Fact]
    public async Task WithoutAConfiguredAddon_NothingIsAskedAndNothingAvailable()
    {
        // The default DI client of the test host has no StudyLifeAi:* configuration.
        var result = await _factory.WithDbAsync(async db =>
        {
            var service = new AiConsentService(db, _factory.Services.GetRequiredService<AiProxyClient>(), demo: false);
            return (await service.GetStatusAsync(1, default), await service.EnsureGrantedAsync(1, default));
        });

        Assert.False(result.Item1.AiAvailable);
        Assert.False(result.Item1.ConsentRequired);
        Assert.Equal(ServiceOutcome.Success, result.Item2.Outcome);
    }

    [Fact]
    public async Task DemoInstance_ReportsAiUnavailable_AndNeverProcessesInTheBackground()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, AiConsentTestHost.ExternalFingerprint());
        var stub = new AiUpstreamStub();

        var (status, caps, mayProcess) = await WithServiceAsync(stub,
            async s => (await s.GetStatusAsync(1, default), await s.GetCapabilitiesAsync(default), await s.MayProcessInBackgroundAsync(1, default)),
            demo: true);

        Assert.False(status.AiAvailable);
        Assert.False(caps.Available);
        Assert.False(mayProcess);
        Assert.Equal(0, stub.Count("/internal/provider-info")); // a demo never even asks the add-on
    }

    // ── covers / does not cover ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoredConsent_ForTheCurrentDescription_Covers()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, AiConsentTestHost.ExternalFingerprint());
        var stub = new AiUpstreamStub();

        var (status, gate) = await WithServiceAsync(stub, async s => (await s.GetStatusAsync(1, default), await s.EnsureGrantedAsync(1, default)));

        Assert.True(status.Granted);
        Assert.NotNull(status.GrantedAt);
        Assert.False(status.NeedsDecision);
        Assert.Equal(ServiceOutcome.Success, gate.Outcome);
    }

    [Fact]
    public async Task StoredConsent_ForAnotherDescription_DoesNotCover_AndAsksAgain()
    {
        // The operator switched provider or model after the user agreed.
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: true, AiConsentTestHost.ExternalFingerprint());
        var stub = new AiUpstreamStub
        {
            ExternalJson = AiUpstreamStub.ExternalInfoJson.Replace("gpt-4o-mini", "gpt-5", StringComparison.Ordinal),
        };

        var (status, gate) = await WithServiceAsync(stub, async s => (await s.GetStatusAsync(1, default), await s.EnsureGrantedAsync(1, default)));

        Assert.False(status.Granted);
        Assert.Null(status.GrantedAt);
        Assert.True(status.NeedsDecision); // has a key, no covering consent: asked at the next app start
        Assert.Equal(ServiceOutcome.Forbidden, gate.Outcome);
    }

    [Fact]
    public void Fingerprint_IgnoresRoleOrder_ButSeesModelProviderAndLocality()
    {
        var a = new AiProviderRole("chat", "OpenAI", "gpt-4o", false);
        var b = new AiProviderRole("embeddings", "Ollama (local)", "nomic", true);

        var original = AiConsentService.Fingerprint(new AiProviderInfo([a, b], true));
        Assert.Equal(original, AiConsentService.Fingerprint(new AiProviderInfo([b, a], true)));
        Assert.NotEqual(original, AiConsentService.Fingerprint(new AiProviderInfo([a with { Model = "gpt-5" }, b], true)));
        Assert.NotEqual(original, AiConsentService.Fingerprint(new AiProviderInfo([a with { Provider = "Azure OpenAI" }, b], true)));
        Assert.NotEqual(original, AiConsentService.Fingerprint(new AiProviderInfo([a with { Local = true }, b], true)));
        Assert.NotEqual(original, AiConsentService.Fingerprint(null));
        Assert.Matches("^[0-9a-f]{64}$", original);
    }

    // ── grant ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Grant_RecordsTimestampAndFingerprint_AndThenCovers()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await WithServiceAsync(stub, s => s.GrantAsync(1, AiConsentTestHost.ExternalFingerprint(), default));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.True(result.Value!.Granted);
        var stored = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().FirstAsync());
        Assert.Equal(AiConsentTestHost.ExternalFingerprint(), stored.AiConsentProviderFingerprint);
        Assert.True(stored.AiConsentGrantedAt >= before);
    }

    [Fact]
    public async Task Grant_ForAStaleDescription_IsAConflictCarryingTheCurrentStatus_AndStoresNothing()
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
        var stub = new AiUpstreamStub();
        var stale = new string('a', 64);

        var result = await WithServiceAsync(stub, s => s.GrantAsync(1, stale, default));

        Assert.Equal(ServiceOutcome.Conflict, result.Outcome);
        Assert.Equal(AiConsentTestHost.ExternalFingerprint(), result.Value!.Fingerprint);
        var stored = await _factory.WithDbAsync(db => db.Settings.AsNoTracking().FirstAsync());
        Assert.Null(stored.AiConsentGrantedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Grant_WithMalformedFingerprint_IsInvalid(string fingerprint)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);

        var result = await WithServiceAsync(new AiUpstreamStub(), s => s.GrantAsync(1, fingerprint, default));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Grant_WithoutAnAddon_IsInvalid()
    {
        var result = await _factory.WithDbAsync(db =>
            new AiConsentService(db, _factory.Services.GetRequiredService<AiProxyClient>(), demo: false)
                .GrantAsync(1, new string('a', 64), default));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Withdraw_WithoutAKeyService_FailsLoudlyInsteadOfSilentlyKeepingTheKey()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithServiceAsync(new AiUpstreamStub(), s => s.WithdrawAsync(1, default)));
    }

    // ── background gate ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, false)] // no key: AI never switched on
    [InlineData(true, false, false)] // key but no consent
    [InlineData(true, true, true)]
    public async Task MayProcessInBackground_NeedsAKeyAndACoveringConsent(bool hasKey, bool consent, bool expected)
    {
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey, consent ? AiConsentTestHost.ExternalFingerprint() : null);

        var may = await WithServiceAsync(new AiUpstreamStub(), s => s.MayProcessInBackgroundAsync(1, default));

        Assert.Equal(expected, may);
    }

    // ── provider description cache (AiProxyClient) ──────────────────────────────────────────

    [Fact]
    public async Task ProviderInfo_IsCached_AndRefetchedAfterTheTtl()
    {
        var stub = new AiUpstreamStub();
        var time = new ManualTimeProvider();
        var client = AiConsentTestHost.CreateClient(stub, time);

        await client.GetProviderInfoAsync(default);
        await client.GetProviderInfoAsync(default);
        Assert.Equal(1, stub.Count("/internal/provider-info"));

        time.Advance(TimeSpan.FromMinutes(6));
        await client.GetProviderInfoAsync(default);
        Assert.Equal(2, stub.Count("/internal/provider-info"));
    }

    [Fact]
    public async Task ProviderInfo_WhenTheAddonGoesAway_KeepsServingTheLastKnownDescription()
    {
        // A short studylife-ai outage must not make every stored consent "not cover" the
        // provider and ask everybody again.
        var stub = new AiUpstreamStub();
        var time = new ManualTimeProvider();
        var client = AiConsentTestHost.CreateClient(stub, time);
        var first = await client.GetProviderInfoAsync(default);
        Assert.NotNull(first);

        stub.Mode = ProviderInfoMode.Unreachable;
        time.Advance(TimeSpan.FromMinutes(6));
        var second = await client.GetProviderInfoAsync(default);

        Assert.Equal(first, second);
        Assert.Equal(2, stub.Count("/internal/provider-info"));
    }

    [Fact]
    public async Task ProviderInfo_AfterAFailure_IsRetriedOnlyAfterTheShortRetryWindow()
    {
        var stub = new AiUpstreamStub { Mode = ProviderInfoMode.NotFound };
        var time = new ManualTimeProvider();
        var client = AiConsentTestHost.CreateClient(stub, time);

        Assert.Null(await client.GetProviderInfoAsync(default));
        Assert.Null(await client.GetProviderInfoAsync(default));
        Assert.Equal(1, stub.Count("/internal/provider-info")); // not hammered

        stub.Mode = ProviderInfoMode.External;
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(await client.GetProviderInfoAsync(default));
    }

    [Fact]
    public async Task ProviderInfo_WithAnEmptyRoleList_IsTreatedAsUnknown()
    {
        var stub = new AiUpstreamStub { ExternalJson = "{\"roles\":[],\"any_external\":false}" };

        Assert.Null(await AiConsentTestHost.CreateClient(stub).GetProviderInfoAsync(default));
    }

    [Fact]
    public async Task ProviderInfo_IsFetchedFromTheInternalEndpoint()
    {
        var stub = new AiUpstreamStub();

        await AiConsentTestHost.CreateClient(stub).GetProviderInfoAsync(default);

        Assert.Contains("GET /internal/provider-info", stub.Requests);
    }
}
