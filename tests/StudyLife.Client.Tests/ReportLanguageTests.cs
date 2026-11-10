// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>
/// AppStateService.ReportLanguageAsync: the client tells the server which UI language it shows,
/// but only when the server does not know it already, and never loudly - offline, an old server
/// and the demo instance's 403 must all end silently.
/// </summary>
public class ReportLanguageTests
{
    /// <summary>Answers GET api/settings with a fixed language and records every PUT to the
    /// language endpoint.</summary>
    private sealed class ServerStub(string? serverLanguage, HttpStatusCode putStatus = HttpStatusCode.NoContent, bool throwOnPut = false)
        : HttpMessageHandler
    {
        public List<string?> Reported { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/api/settings/language", StringComparison.Ordinal))
            {
                if (throwOnPut) throw new HttpRequestException("offline");
                var body = await request.Content!.ReadFromJsonAsync<SetLanguageRequest>(cancellationToken: cancellationToken);
                Reported.Add(body!.Language);
                return new HttpResponseMessage(putStatus);
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/api/settings", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new UserSettingsDto { Version = 3, Language = serverLanguage }),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static AppStateService Build(ServerStub stub)
    {
        var js = new StubJSRuntime();
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://studylife.test/") };
        // No token: the constructor then does not open the change stream.
        return new AppStateService(http, js, NullLogger<AppStateService>.Instance, new SessionTokenStore(js));
    }

    [Fact]
    public async Task Reports_the_language_when_the_server_has_none()
    {
        var stub = new ServerStub(serverLanguage: null);
        await using var state = Build(stub);

        await state.ReportLanguageAsync("fr");

        Assert.Equal(new[] { "fr" }, stub.Reported);
        Assert.Equal("fr", (await state.GetSettingsAsync()).Language);
    }

    [Fact]
    public async Task Reports_the_language_when_the_server_has_another_one()
    {
        var stub = new ServerStub(serverLanguage: "de");
        await using var state = Build(stub);

        await state.ReportLanguageAsync("en");

        Assert.Equal(new[] { "en" }, stub.Reported);
    }

    [Fact]
    public async Task Does_nothing_when_the_server_already_has_this_language()
    {
        var stub = new ServerStub(serverLanguage: "pt");
        await using var state = Build(stub);

        await state.ReportLanguageAsync("pt");

        Assert.Empty(stub.Reported);
    }

    [Fact]
    public async Task Reports_only_once_for_repeated_calls()
    {
        var stub = new ServerStub(serverLanguage: null);
        await using var state = Build(stub);

        await state.ReportLanguageAsync("it");
        await state.ReportLanguageAsync("it");

        Assert.Single(stub.Reported);
    }

    [Fact]
    public async Task Never_sends_an_unsupported_language()
    {
        var stub = new ServerStub(serverLanguage: null);
        await using var state = Build(stub);

        await state.ReportLanguageAsync("xx");
        await state.ReportLanguageAsync("");

        Assert.Empty(stub.Reported);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]          // demo instance: write block
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]           // server older than this endpoint
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_rejection_is_silent_and_leaves_the_local_state_alone(HttpStatusCode status)
    {
        var stub = new ServerStub(serverLanguage: null, putStatus: status);
        await using var state = Build(stub);

        await state.ReportLanguageAsync("fr");

        Assert.Null((await state.GetSettingsAsync()).Language);
    }

    [Fact]
    public async Task Being_offline_is_silent()
    {
        var stub = new ServerStub(serverLanguage: null, throwOnPut: true);
        await using var state = Build(stub);

        await state.ReportLanguageAsync("fr");

        Assert.Null((await state.GetSettingsAsync()).Language);
    }
}
