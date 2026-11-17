// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>Legal:PrivacyUrl / Legal:ImprintUrl: startup validation (docs/operator-privacy-template.md).</summary>
public class LegalOptionsTests
{
    private static LegalOptions Load(params (string Key, string? Value)[] settings) =>
        TestOptions.For<LegalOptions>(settings).Value;

    [Fact]
    public void Nothing_set_is_valid_and_means_the_publishers_pages()
    {
        var options = Load();

        Assert.Null(LegalOptions.Normalize(options.PrivacyUrl));
        Assert.Null(LegalOptions.Normalize(options.ImprintUrl));
    }

    [Theory]
    [InlineData("https://example.org/privacy")]
    [InlineData("  https://example.org/privacy  ")]
    [InlineData("http://localhost:8080/privacy")]
    [InlineData("http://127.0.0.1/privacy")]
    [InlineData("http://[::1]/privacy")]
    public void Valid_addresses_are_accepted(string url)
    {
        var options = Load(("Legal:PrivacyUrl", url), ("Legal:ImprintUrl", url));

        Assert.Equal(url.Trim(), LegalOptions.Normalize(options.PrivacyUrl));
        Assert.Equal(url.Trim(), LegalOptions.Normalize(options.ImprintUrl));
    }

    [Theory]
    [InlineData("example.org/privacy")]
    [InlineData("/privacy")]
    [InlineData("http://example.org/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.org/privacy")]
    [InlineData("https://user:pw@example.org/privacy")]
    public void Invalid_addresses_stop_the_start_with_the_key_in_the_message(string url)
    {
        var privacy = Assert.Throws<OptionsValidationException>(() => Load(("Legal:PrivacyUrl", url)));
        Assert.Contains("Legal:PrivacyUrl", privacy.Message);

        var imprint = Assert.Throws<OptionsValidationException>(() => Load(("Legal:ImprintUrl", url)));
        Assert.Contains("Legal:ImprintUrl", imprint.Message);
    }

    [Fact]
    public void Blank_values_count_as_unset()
    {
        var options = Load(("Legal:PrivacyUrl", "  "), ("Legal:ImprintUrl", ""));

        Assert.Null(LegalOptions.Normalize(options.PrivacyUrl));
        Assert.Null(LegalOptions.Normalize(options.ImprintUrl));
    }
}

/// <summary>GET /api/instance without operator settings: both addresses stay null.</summary>
public class InstanceLegalDefaultTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InstanceLegalDefaultTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_ExposesNoOperatorAddresses_ByDefault()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var body = await anonymous.GetFromJsonAsync<InstanceInfoDto>("/api/instance");

        Assert.NotNull(body!.Legal);
        Assert.Null(body.Legal!.PrivacyUrl);
        Assert.Null(body.Legal.ImprintUrl);
    }
}

/// <summary>GET /api/instance with operator settings: the client reads them before sign-in.</summary>
public class InstanceLegalConfiguredTests : IClassFixture<InstanceLegalConfiguredTests.OperatorLegalFactory>
{
    public sealed class OperatorLegalFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Legal:PrivacyUrl"] = "https://operator.example.org/privacy",
                    ["Legal:ImprintUrl"] = " https://operator.example.org/imprint ",
                }));
        }
    }

    private readonly OperatorLegalFactory _factory;

    public InstanceLegalConfiguredTests(OperatorLegalFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_ExposesTheOperatorAddresses_Anonymously()
    {
        using var anonymous = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var body = await anonymous.GetFromJsonAsync<InstanceInfoDto>("/api/instance");

        Assert.Equal("https://operator.example.org/privacy", body!.Legal!.PrivacyUrl);
        Assert.Equal("https://operator.example.org/imprint", body.Legal.ImprintUrl);
    }
}

/// <summary>A bad Legal:* setting refuses to start the whole server.</summary>
public class InstanceLegalInvalidStartupTests
{
    private sealed class BadLegalFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Legal:PrivacyUrl"] = "not a url" }));
        }
    }

    [Fact]
    public void Startup_FailsFast_OnAnInvalidAddress()
    {
        using var factory = new BadLegalFactory();

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Legal:PrivacyUrl", failure.ToString());
    }
}
