// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>How the stubbed studylife-ai answers GET /internal/provider-info.</summary>
public enum ProviderInfoMode
{
    External,
    Local,
    /// <summary>404, like a studylife-ai that predates the endpoint.</summary>
    NotFound,
    /// <summary>The call fails at the transport level.</summary>
    Unreachable,
}

/// <summary>A stand-in for the studylife-ai add-on: answers the internal endpoints and the three
/// proxy paths, and records every request (method and path) so tests can assert what was - and,
/// for the consent rule, what was NOT - sent.</summary>
internal sealed class AiUpstreamStub : HttpMessageHandler
{
    public const string ExternalInfoJson =
        "{\"roles\":[{\"role\":\"chat\",\"provider\":\"OpenAI\",\"model\":\"gpt-4o-mini\",\"local\":false}," +
        "{\"role\":\"embeddings\",\"provider\":\"Ollama (local)\",\"model\":\"nomic-embed-text\",\"local\":true}],\"any_external\":true}";

    public const string LocalInfoJson =
        "{\"roles\":[{\"role\":\"chat\",\"provider\":\"Ollama (local)\",\"model\":\"llama3.2\",\"local\":true}],\"any_external\":false}";

    public ProviderInfoMode Mode { get; set; } = ProviderInfoMode.External;
    public string ExternalJson { get; set; } = ExternalInfoJson;
    public List<string> Requests { get; } = [];

    public int Count(string path) { lock (Requests) return Requests.Count(r => r.EndsWith(path, StringComparison.Ordinal)); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add($"{request.Method} {path}");

        if (path == "/internal/provider-info")
        {
            return Task.FromResult(Mode switch
            {
                ProviderInfoMode.External => Json(ExternalJson),
                ProviderInfoMode.Local => Json(LocalInfoJson),
                ProviderInfoMode.NotFound => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new HttpRequestException("studylife-ai unreachable (test)"),
            });
        }
        if (path == "/internal/enrich-capture")
            return Task.FromResult(Json("{\"course_id\":null,\"course_confidence\":null,\"tags\":[],\"summary\":null,\"related_note_ids\":[]}"));
        return Task.FromResult(Json("{}"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}

/// <summary>A clock the test advances by hand, to step over the provider-description cache TTL.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal static class AiConsentTestHost
{
    public static AiProxyClient CreateClient(AiUpstreamStub stub, TimeProvider? time = null) =>
        new(TestOptions.For<StudyLifeAiOptions>(Config()), NullLogger<AiProxyClient>.Instance, new HttpClient(stub), time);

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["StudyLifeAi:BaseUrl"] = "https://ai-consent-test.invalid",
            ["StudyLifeAi:SharedSecret"] = "shared-secret",
        }).Build();

    /// <summary>A session client of the shared factory whose studylife-ai is the given stub.</summary>
    public static HttpClient CreateHttpClient(CustomWebApplicationFactory factory, AiUpstreamStub stub, TimeProvider? time = null)
    {
        var overridden = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(AiProxyClient));
                services.AddSingleton(CreateClient(stub, time));
            }));
        return overridden.CreateClient();
    }

    /// <summary>Puts the seeded test user (id 1) into a known state: key present or not, consent
    /// recorded for the given fingerprint or not. Tests of one class share a database, so every
    /// test starts with this.</summary>
    public static Task SeedUserAsync(CustomWebApplicationFactory factory, bool hasKey, string? consentFingerprint) =>
        factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.AiApiKeyHash = hasKey ? "test-hash" : null;
            user.AiApiKeyCreatedAt = hasKey ? DateTime.UtcNow : null;
            var settings = await db.Settings.GetOrCreateAsync(db);
            settings.AiConsentGrantedAt = consentFingerprint is null ? null : DateTime.UtcNow;
            settings.AiConsentProviderFingerprint = consentFingerprint;
            await db.SaveChangesAsync();
        });

    public static string ExternalFingerprint() => AiConsentService.Fingerprint(ParseExternal(AiUpstreamStub.ExternalInfoJson));

    public static string UnknownFingerprint() => AiConsentService.Fingerprint(null);

    private static AiProviderInfo ParseExternal(string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var roles = doc.GetProperty("roles").EnumerateArray()
            .Select(r => new AiProviderRole(
                r.GetProperty("role").GetString()!, r.GetProperty("provider").GetString()!,
                r.GetProperty("model").GetString()!, r.GetProperty("local").GetBoolean()))
            .ToList();
        return new AiProviderInfo(roles, doc.GetProperty("any_external").GetBoolean());
    }
}
