// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>The stable installation id (InstanceIdProvider + GET /api/instance, docs/MDNS.md).</summary>
public partial class InstanceIdProviderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InstanceIdProviderTests(CustomWebApplicationFactory factory) => _factory = factory;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex32();

    private InstanceIdProvider NewProvider() =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>());

    private Task<int> RowCountAsync() => _factory.WithDbAsync(db => db.InstanceInfo.CountAsync());

    [Fact]
    public async Task Get_CreatesAnId_AndReturnsTheSameOneOnRepeatedCalls()
    {
        var provider = NewProvider();

        var first = await provider.GetAsync();
        var second = await provider.GetAsync();

        Assert.Matches(Hex32(), first);
        Assert.Equal(first, second);
        Assert.Equal(1, await RowCountAsync());
    }

    [Fact]
    public async Task Get_NewProviderOnTheSameDatabase_ReturnsThePersistedId()
    {
        var first = await NewProvider().GetAsync();

        var fromNewProvider = await NewProvider().GetAsync();

        Assert.Equal(first, fromNewProvider);
        Assert.Equal(first, await _factory.WithDbAsync(db =>
            db.InstanceInfo.Where(i => i.Key == InstanceInfoEntity.InstanceIdKey).Select(i => i.Value).SingleAsync()));
    }

    [Fact]
    public async Task Get_ConcurrentFirstCallsAcrossProviders_ProduceExactlyOneId()
    {
        // Several independent providers = several replicas starting at once on one database.
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => NewProvider().GetAsync())));

        Assert.Single(ids.Distinct());
        Assert.Matches(Hex32(), ids[0]);
        Assert.Equal(1, await RowCountAsync());
    }

    [Fact]
    public async Task Get_ConcurrentCallsOnOneProvider_ShareOneId()
    {
        var provider = NewProvider();

        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => provider.GetAsync()));

        Assert.Single(ids.Distinct());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidId_AcceptsOnly32LowercaseHex(string? value, bool expected) =>
        Assert.Equal(expected, InstanceIdProvider.IsValidId(value));
}

public partial class InstanceEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InstanceEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex32();

    [Fact]
    public async Task Get_WithoutAnyCredential_ReturnsIdAndVersion()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anonymous.GetAsync("/api/instance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.CacheControl?.MaxAge);
        var body = await response.Content.ReadFromJsonAsync<InstanceInfoDto>();
        Assert.Matches(Hex32(), body!.Id);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
        Assert.DoesNotContain('+', body.Version);
    }

    [Fact]
    public async Task Get_IsStableAcrossRequests_AndEqualForSessionClients()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var session = _factory.CreateClient();

        var a = await anonymous.GetFromJsonAsync<InstanceInfoDto>("/api/instance");
        var b = await session.GetFromJsonAsync<InstanceInfoDto>("/api/instance");

        Assert.Equal(a!.Id, b!.Id);
    }

    [Fact]
    public async Task Get_HasNoPasskeyDomain_WhenNoneIsPinned()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var body = await anonymous.GetFromJsonAsync<InstanceInfoDto>("/api/instance");

        Assert.Null(body!.PasskeyRpId);
        Assert.Null(body.PasskeyOrigin);
    }
}

/// <summary>GET /api/instance names the pinned passkey domain (the app's hint for internal addresses).</summary>
public class InstancePasskeyDomainTests : IClassFixture<InstancePasskeyDomainTests.PinnedPasskeyFactory>
{
    public sealed class PinnedPasskeyFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Fido2:ServerDomain"] = "studylife.example.com",
                    ["Fido2:Origins:0"] = "https://studylife.example.com",
                }));
        }
    }

    private readonly PinnedPasskeyFactory _factory;

    public InstancePasskeyDomainTests(PinnedPasskeyFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_NamesThePinnedPasskeyDomain()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var body = await anonymous.GetFromJsonAsync<InstanceInfoDto>("/api/instance");

        Assert.Equal("studylife.example.com", body!.PasskeyRpId);
        Assert.Equal("https://studylife.example.com", body.PasskeyOrigin);
    }
}
