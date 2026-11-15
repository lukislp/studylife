// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>
/// Regression for the Planner "accept plan" crash (ArgumentNullException, 2026-11-15): every
/// SaveSessionAsync invalidates the sessions cache and fires change events, whose subscribers
/// fetch the sessions again. A save that landed while such a fetch was still in flight made the
/// fetch return the (just nulled) cache field - null - which the Planner then fed to LINQ.
/// </summary>
public class SessionsCacheRaceTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private sealed class GatedJs : IJSRuntime
    {
        public TaskCompletionSource? SetItemGate { get; set; }
        public TaskCompletionSource SetItemReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "localStorage.setItem" && args is [string key, ..] && key.StartsWith("studylife-cache-sessions-v2", StringComparison.Ordinal)
                && SetItemGate is { } gate)
            {
                SetItemReached.TrySetResult();
                await gate.Task;
            }
            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>Server stand-in: the sessions list is whatever was POSTed so far; the n-th GET can
    /// be held back until released, which is how a fetch stays "in flight" during a save.</summary>
    private sealed class ServerHandler : HttpMessageHandler
    {
        private readonly List<StudySessionDto> _stored = new();
        public TaskCompletionSource? FirstGetGate { get; set; }
        public TaskCompletionSource FirstGetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Gets { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/events", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (path.EndsWith("/api/sessions", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var n = ++Gets;
                var snapshot = _stored.ToList(); // the state this response was computed from
                if (n == 1 && FirstGetGate is { } gate)
                {
                    FirstGetReached.TrySetResult();
                    await gate.Task;
                }
                return Json(snapshot, request);
            }
            if (path.EndsWith("/api/sessions", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var dto = (await request.Content!.ReadFromJsonAsync<StudySessionDto>(StudyLifeJson.Options, ct))!;
                dto.Id = _stored.Count + 1;
                _stored.Add(dto);
                return Json(dto, request);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        private static HttpResponseMessage Json<T>(T value, HttpRequestMessage request) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: StudyLifeJson.Options),
            RequestMessage = request,
        };
    }

    private static AppStateService Build(IJSRuntime js, HttpMessageHandler handler)
    {
        var store = new SessionTokenStore(js);
        store.SetTokenAsync("test-token").GetAwaiter().GetResult();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://studylife.test/") };
        return new AppStateService(http, js, NullLogger<AppStateService>.Instance, store);
    }

    private static StudySession NewSession() => new()
    {
        CourseId = 1,
        CourseName = "Cloud Computing",
        CourseColor = "#fff",
        StartTime = DateTime.Today.AddDays(1).AddHours(9),
        EndTime = DateTime.Today.AddDays(1).AddHours(10),
        Topic = "t",
        TimerModeId = 1,
    };

    [Fact]
    public async Task A_save_while_the_sessions_fetch_is_awaiting_its_cache_write_never_yields_null()
    {
        var js = new GatedJs { SetItemGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var server = new ServerHandler();
        await using var state = Build(js, server);

        var fetch = state.GetSessionsAsync();
        await js.SetItemReached.Task.WaitAsync(SafetyNet); // response is in, the read-cache write is pending

        var save = state.SaveSessionAsync(NewSession());
        js.SetItemGate!.SetResult();
        Assert.True((await save.WaitAsync(SafetyNet)).Success);

        var sessions = await fetch.WaitAsync(SafetyNet);

        Assert.NotNull(sessions);
        Assert.Single(sessions); // the fetch noticed the save and asked again instead of serving the old answer
    }

    [Fact]
    public async Task A_fetch_that_was_in_flight_during_a_save_does_not_cache_its_stale_answer()
    {
        var js = new GatedJs();
        var server = new ServerHandler { FirstGetGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var state = Build(js, server);

        var fetch = state.GetSessionsAsync();
        await server.FirstGetReached.Task.WaitAsync(SafetyNet);

        Assert.True((await state.SaveSessionAsync(NewSession()).WaitAsync(SafetyNet)).Success);
        server.FirstGetGate!.SetResult(); // the held response predates the save: it is empty

        Assert.Single(await fetch.WaitAsync(SafetyNet));
        Assert.Single(await state.GetSessionsAsync().WaitAsync(SafetyNet));
    }
}
