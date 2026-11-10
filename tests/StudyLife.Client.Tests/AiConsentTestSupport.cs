// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>A scripted server: answers by "METHOD path" and records what was asked.</summary>
internal sealed class ScriptedServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
    public List<string> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    public int Count(string methodAndPath) => Requests.Count(r => r == methodAndPath);

    public ScriptedServer On(string methodAndPath, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _routes[methodAndPath] = responder;
        return this;
    }

    public ScriptedServer OnJson(string methodAndPath, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(methodAndPath, _ => new HttpResponseMessage(status) { Content = JsonContent.Create(body) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var key = $"{request.Method} {path}";
        lock (Requests) Requests.Add(key);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies) Bodies.Add(body);
        }
        return _routes.TryGetValue(key, out var responder) ? responder(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal static class AiConsentFixtures
{
    public static string Fingerprint(char c = 'a') => new(c, 64);

    public static AiConsentStatusDto Status(
        bool available = true, bool required = true, bool granted = false, bool hasKey = false,
        bool known = true, char fingerprint = 'a', bool needsDecision = false, DateTime? grantedAt = null) => new()
        {
            AiAvailable = available,
            ConsentRequired = required,
            Granted = granted,
            GrantedAt = granted ? grantedAt ?? new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc) : null,
            HasKey = hasKey,
            ProviderKnown = known,
            Fingerprint = Fingerprint(fingerprint),
            NeedsDecision = needsDecision,
            Provider = known
            ? new AiProviderInfoDto
            {
                AnyExternal = required,
                Roles =
                [
                    new AiProviderRoleDto { Role = "chat", Provider = "OpenAI", Model = "gpt-4o-mini", Local = false },
                    new AiProviderRoleDto { Role = "embeddings", Provider = "OpenAI", Model = "gpt-4o-mini", Local = false },
                    new AiProviderRoleDto { Role = "rerank", Provider = "Ollama (local)", Model = "llama3.2", Local = true },
                ],
            }
            : null,
        };

    public static AiConsentState NewState(ScriptedServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });

    /// <summary>An AppStateService on the scripted server (no token, so no change stream starts).</summary>
    public static AppStateService NewAppState(ScriptedServer server)
    {
        var js = new StubJSRuntime();
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        return new AppStateService(http, js, NullLogger<AppStateService>.Instance, new SessionTokenStore(js));
    }

    /// <summary>Registers what the Setup card and the chat modal inject: the scripted HttpClient,
    /// an AppStateService on it, and the I18nText loader. I18nText fetches its tables through the
    /// browser (JS interop), which bUnit answers with nothing, so components render their text
    /// KEYS here ("LocalRunsHere", ...) - the generated table types make a wrong key a compile
    /// error, and the key sets of all 26 languages are checked by tools/check-i18n.py.</summary>
    public static AppStateService Register(BunitContext ctx, ScriptedServer server)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = NewAppState(server);
        ctx.Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        ctx.Services.AddSingleton(state);
        ctx.Services.AddI18nText();
        return state;
    }

    public static string ProblemJson(string type = "ai-consent-required") =>
        JsonSerializer.Serialize(new { type, title = type, status = 403 });
}
