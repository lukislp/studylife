// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;

namespace StudyLife.Server.Tests;

/// <summary>
/// The account-deletion purge call to studylife-mcp/-alexa/-telegram (AddonPurgeClient) and its
/// configuration (AddonPurge:*, AddonPurgeOptionsValidator). The contract is the one the add-ons
/// implement: POST /internal/users/{id}/purge, X-StudyLife-Shared-Secret, {"key_hashes": [...]}.
/// </summary>
public class AddonPurgeClientTests
{
    private static readonly string HashA = AuthSessionService.HashToken("purge-client-key-a");
    private static readonly string HashB = AuthSessionService.HashToken("purge-client-key-b");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Secret, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var secret = request.Headers.TryGetValues("X-StudyLife-Shared-Secret", out var values) ? values.Single() : null;
            Requests.Add((request.Method, request.RequestUri!.ToString(), secret, body));
            return respond(request);
        }
    }

    private static (AddonPurgeClient Client, RecordingHandler Handler) Client(
        AddonPurgeTarget target, Func<HttpRequestMessage, HttpResponseMessage> respond, string? baseUrl = "https://mcp.test/", string? secret = "purge-secret")
    {
        var handler = new RecordingHandler(respond);
        var options = new AddonPurgeOptions { BaseUrl = baseUrl, SharedSecret = secret };
        return (new AddonPurgeClient(target, options, NullLogger<AddonPurgeClient>.Instance, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Purge_PostsTheContractRequest_AndTreats204AsDelivered()
    {
        var (client, handler) = Client(AddonPurgeTarget.Mcp, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.True(await client.PurgeUserAsync(7, [HashA, HashB], CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://mcp.test/internal/users/7/purge", request.Uri);
        Assert.Equal("purge-secret", request.Secret);
        Assert.Equal($"{{\"key_hashes\":[\"{HashA}\",\"{HashB}\"]}}", request.Body);
    }

    [Fact]
    public async Task Purge_WithAPinnedInstanceUrl_SendsItNextToTheHashes()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new AddonPurgeClient(AddonPurgeTarget.Alexa,
            new AddonPurgeOptions { BaseUrl = "https://alexa.test", SharedSecret = "purge-secret" },
            NullLogger<AddonPurgeClient>.Instance, new HttpClient(handler), () => "https://studyatrium.test");

        Assert.True(await client.PurgeUserAsync(7, [], CancellationToken.None));
        Assert.Equal("{\"key_hashes\":[],\"instance_url\":\"https://studyatrium.test\"}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Purge_WithoutHashes_SendsAnEmptyList()
    {
        var (client, handler) = Client(AddonPurgeTarget.Mcp, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.True(await client.PurgeUserAsync(7, [], CancellationToken.None));
        Assert.Equal("{\"key_hashes\":[]}", Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)] // an add-on version without the route
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)] // the add-on has no purge secret yet
    public async Task Purge_AnyNon2xx_IsNotDelivered(HttpStatusCode status)
    {
        var (client, _) = Client(AddonPurgeTarget.Alexa, _ => new HttpResponseMessage(status));

        Assert.False(await client.PurgeUserAsync(7, [HashA], CancellationToken.None));
    }

    [Fact]
    public async Task Purge_TransportError_IsNotDelivered_AndDoesNotThrow()
    {
        var (client, _) = Client(AddonPurgeTarget.Telegram, _ => throw new HttpRequestException("connection refused"));

        Assert.False(await client.PurgeUserAsync(7, [HashA], CancellationToken.None));
    }

    [Fact]
    public async Task Purge_NotConfigured_CountsAsDelivered_WithoutAnyRequest()
    {
        var (client, handler) = Client(AddonPurgeTarget.Mcp, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            baseUrl: null, secret: null);

        Assert.False(client.Configured);
        Assert.True(await client.PurgeUserAsync(7, [HashA], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Purge_UrlWithoutSecret_StaysQueued_WithoutAnyRequest()
    {
        // The add-on is part of the installation, but the secret has not been rolled out yet:
        // the purge must wait for it, not count as done.
        var (client, handler) = Client(AddonPurgeTarget.Mcp, _ => new HttpResponseMessage(HttpStatusCode.NoContent), secret: null);

        Assert.True(client.Configured);
        Assert.False(await client.PurgeUserAsync(7, [HashA], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://studylife-mcp.local:8000", true)]
    [InlineData("HTTP://studylife-mcp.local:8000", true)]
    [InlineData("https://studylife-mcp.local:8443", false)]
    public void PlainHttpBaseUrl_LogsAWarning_ButHttpsDoesNot(string baseUrl, bool warns)
    {
        var logger = new CapturingLogger<AddonPurgeClient>();
        _ = new AddonPurgeClient(AddonPurgeTarget.Mcp, new AddonPurgeOptions { BaseUrl = baseUrl, SharedSecret = "purge-secret" }, logger);

        var plainHttpWarnings = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("plain HTTP", StringComparison.Ordinal)).ToList();
        Assert.Equal(warns, plainHttpWarnings.Count == 1);
        Assert.DoesNotContain(plainHttpWarnings, e => e.Everything.Contains("purge-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void NoBaseUrl_NoPlainHttpWarning()
    {
        var logger = new CapturingLogger<AddonPurgeClient>();
        _ = new AddonPurgeClient(AddonPurgeTarget.Mcp, new AddonPurgeOptions(), logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ClientId_DefaultsPerAddon_AndCanBeOverridden()
    {
        var logger = NullLogger<AddonPurgeClient>.Instance;
        Assert.Null(new AddonPurgeClient(AddonPurgeTarget.Mcp, new AddonPurgeOptions(), logger).ClientId);
        Assert.Equal("studylife-alexa", new AddonPurgeClient(AddonPurgeTarget.Alexa, new AddonPurgeOptions(), logger).ClientId);
        Assert.Equal("studylife-telegram", new AddonPurgeClient(AddonPurgeTarget.Telegram, new AddonPurgeOptions { ClientId = " " }, logger).ClientId);
        Assert.Equal("my-telegram-bot", new AddonPurgeClient(AddonPurgeTarget.Telegram, new AddonPurgeOptions { ClientId = "my-telegram-bot" }, logger).ClientId);
    }

    [Fact]
    public void Registration_BindsOneNamedSectionPerAddon()
    {
        var options = TestOptions.MonitorFor<AddonPurgeOptions>(
            ("AddonPurge:Mcp:BaseUrl", "http://studylife-mcp:8000"),
            ("AddonPurge:Mcp:SharedSecret", "mcp-secret"),
            ("AddonPurge:Alexa:BaseUrl", "http://studylife-alexa:8000"),
            ("AddonPurge:Alexa:SharedSecret", "alexa-secret"),
            ("AddonPurge:Alexa:ClientId", "alexa-custom"));

        var clients = AddonPurgeClients.Create(options, NullLogger<AddonPurgeClient>.Instance, () => new HttpClient());

        Assert.Equal(AddonPurgeTarget.All, clients.All.Select(c => c.Target));
        Assert.True(clients.For(AddonPurgeTarget.Mcp)!.Configured);
        Assert.True(clients.For(AddonPurgeTarget.Alexa)!.Configured);
        Assert.Equal("alexa-custom", clients.For(AddonPurgeTarget.Alexa)!.ClientId);
        Assert.False(clients.For(AddonPurgeTarget.Telegram)!.Configured);
    }

    [Theory]
    [InlineData("studylife-mcp:8000")] // not an absolute URL
    [InlineData("ftp://studylife-mcp")]
    public void Validation_RefusesABrokenUrl(string baseUrl)
    {
        var options = TestOptions.MonitorFor<AddonPurgeOptions>(("AddonPurge:Mcp:BaseUrl", baseUrl), ("AddonPurge:Mcp:SharedSecret", "secret"));

        var ex = Assert.Throws<OptionsValidationException>(() => options.Get(AddonPurgeTarget.Mcp.Name));
        Assert.Contains("AddonPurge:Mcp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_RequireHttps_RefusesPlainHttp_UnlessTheDeploymentAllowsItExplicitly()
    {
        var refused = TestOptions.MonitorFor<AddonPurgeOptions>(
            ("AddonTls:RequireHttps", "true"),
            ("AddonPurge:Telegram:BaseUrl", "http://studylife-telegram:8000"),
            ("AddonPurge:Telegram:SharedSecret", "secret"));
        var ex = Assert.Throws<OptionsValidationException>(() => refused.Get(AddonPurgeTarget.Telegram.Name));
        Assert.Contains("AddonPurge:Telegram:BaseUrl", ex.Message, StringComparison.Ordinal);

        var allowed = TestOptions.MonitorFor<AddonPurgeOptions>(
            ("AddonTls:RequireHttps", "true"),
            ("AddonPurge:Telegram:BaseUrl", "http://studylife-telegram:8000"),
            ("AddonPurge:Telegram:SharedSecret", "secret"),
            ("AddonPurge:Telegram:AllowPlainHttp", "true"));
        Assert.True(allowed.Get(AddonPurgeTarget.Telegram.Name).AllowPlainHttp);

        var https = TestOptions.MonitorFor<AddonPurgeOptions>(
            ("AddonTls:RequireHttps", "true"),
            ("AddonPurge:Telegram:BaseUrl", "https://studylife-telegram:8443"),
            ("AddonPurge:Telegram:SharedSecret", "secret"));
        Assert.Equal("https://studylife-telegram:8443", https.Get(AddonPurgeTarget.Telegram.Name).BaseUrl);
    }

    [Fact]
    public void Validation_NothingConfigured_IsValid_AndUnknownNamesAreSkipped()
    {
        var options = TestOptions.MonitorFor<AddonPurgeOptions>();
        foreach (var target in AddonPurgeTarget.All)
            Assert.Null(options.Get(target.Name).BaseUrl);

        var validator = new AddonPurgeOptionsValidator(Options.Create(new AddonTlsOptions()));
        Assert.True(validator.Validate("SomethingElse", new AddonPurgeOptions { BaseUrl = "x" }).Skipped);
    }
}
