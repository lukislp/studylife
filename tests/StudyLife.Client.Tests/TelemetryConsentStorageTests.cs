// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>
/// TDDDG 25 contract of TelemetryService: nothing telemetry-related is stored on the device while
/// the consent is null or false (session id in localStorage, cold/warm flag in sessionStorage,
/// native queue via INativeTelemetry.ApplyConsentAsync), it appears once the consent is true and
/// disappears on decline, withdrawal and sign-out.
/// </summary>
public class TelemetryConsentStorageTests
{
    private const string SessionKey = "studylife-telemetry-session";
    private const string BootedBeforeKey = "studylife-booted-before";

    private sealed class Harness : IAsyncDisposable
    {
        public FakeStorageJs Js { get; } = new();
        public SessionTokenStore Store { get; }
        public AppStateService State { get; }
        public RecordingNativeTelemetry Native { get; } = new();
        public TelemetryService Telemetry { get; }
        public SettingsServer Server { get; }

        public Harness(bool? consent)
        {
            Server = new SettingsServer(consent);
            Store = new SessionTokenStore(Js);
            var http = new HttpClient(Server) { BaseAddress = new Uri("https://studylife.test/") };
            State = new AppStateService(http, Js, NullLogger<AppStateService>.Instance, Store);
            Telemetry = new TelemetryService(http, Js, State, Native, new BrowserClientPlatform(), () => Task.FromResult("en"));
        }

        public async Task SetConsentAsync(bool? consent)
        {
            Server.Consent = consent;
            var settings = await State.GetSettingsAsync();
            settings.TelemetryConsent = consent;
            await State.SaveSettingsAsync(settings);
            await Telemetry.PendingConsentChange;
        }

        public async ValueTask DisposeAsync()
        {
            await Telemetry.DisposeAsync();
            await State.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task No_session_id_is_written_while_consent_is_not_true(bool? consent)
    {
        await using var h = new Harness(consent);

        await h.Telemetry.InitializeAsync();
        await h.Telemetry.RecordBootFromMarksAsync();

        Assert.Null(h.Js.Local(SessionKey));
        Assert.DoesNotContain(h.Js.Writes, w => w.Key.Contains("telemetry"));
        Assert.Equal(false, h.Js.LastBootFlagPersist);
    }

    [Fact]
    public async Task A_legacy_session_key_is_removed_when_consent_is_unknown()
    {
        await using var h = new Harness(null);
        h.Js.SetLocal(SessionKey, "{\"Id\":\"abcdefghijklmnop\",\"Sampled\":true,\"CreatedAt\":1,\"Rate\":0.1}");

        await h.Telemetry.InitializeAsync();

        Assert.Null(h.Js.Local(SessionKey));
    }

    [Fact]
    public async Task The_session_id_is_created_at_init_when_consent_is_already_true()
    {
        await using var h = new Harness(true);

        await h.Telemetry.InitializeAsync();

        Assert.False(string.IsNullOrEmpty(h.Js.Local(SessionKey)));
    }

    [Fact]
    public async Task The_session_id_is_created_after_the_grant_and_removed_on_decline()
    {
        await using var h = new Harness(null);
        await h.Telemetry.InitializeAsync();
        Assert.Null(h.Js.Local(SessionKey));

        await h.SetConsentAsync(true);
        var stored = h.Js.Local(SessionKey);
        Assert.False(string.IsNullOrEmpty(stored));

        await h.SetConsentAsync(false);
        Assert.Null(h.Js.Local(SessionKey));
    }

    [Fact]
    public async Task The_cold_warm_flag_is_only_persisted_with_consent_and_removed_on_decline()
    {
        await using var h = new Harness(true);
        await h.Telemetry.InitializeAsync();
        await h.Telemetry.RecordBootFromMarksAsync();
        Assert.Equal(true, h.Js.LastBootFlagPersist);

        h.Js.SetSession(BootedBeforeKey, "1");
        await h.SetConsentAsync(false);

        Assert.Null(h.Js.Session(BootedBeforeKey));
    }

    [Fact]
    public async Task The_native_host_is_told_the_effective_consent_at_every_step()
    {
        await using var h = new Harness(null);

        await h.Telemetry.InitializeAsync();
        Assert.Equal(new[] { false }, h.Native.Calls); // undecided counts as false

        await h.SetConsentAsync(true);
        Assert.Equal(new[] { false, true }, h.Native.Calls);

        await h.SetConsentAsync(false); // withdrawal
        Assert.Equal(new[] { false, true, false }, h.Native.Calls);

        await h.SetConsentAsync(true);
        await h.Store.ClearAsync(); // sign-out
        Assert.Equal(new[] { false, true, false, true, false }, h.Native.Calls);
    }

    [Fact]
    public async Task Sign_out_removes_the_session_id_and_the_cold_warm_flag()
    {
        await using var h = new Harness(true);
        await h.Telemetry.InitializeAsync();
        h.Js.SetSession(BootedBeforeKey, "1");
        Assert.NotNull(h.Js.Local(SessionKey));

        await h.Store.ClearAsync();

        Assert.Null(h.Js.Local(SessionKey));
        Assert.Null(h.Js.Session(BootedBeforeKey));
    }

    [Fact]
    public async Task A_settings_change_while_consent_is_still_undecided_does_not_call_the_native_host_again()
    {
        await using var h = new Harness(null);
        await h.Telemetry.InitializeAsync();

        await h.SetConsentAsync(null);

        Assert.Equal(new[] { false }, h.Native.Calls);
    }

    private sealed class RecordingNativeTelemetry : INativeTelemetry
    {
        public List<bool> Calls { get; } = new();

        public Task ApplyConsentAsync(bool granted)
        {
            Calls.Add(granted);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers GET/PUT api/settings from a mutable consent value, everything else 404.</summary>
    private sealed class SettingsServer(bool? consent) : HttpMessageHandler
    {
        public bool? Consent { get; set; } = consent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/settings")
            {
                var json = JsonSerializer.Serialize(new UserSettingsDto { TelemetryConsent = Consent }, StudyLifeJson.Options);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>In-memory localStorage/sessionStorage that records every write.</summary>
    private sealed class FakeStorageJs : IJSRuntime
    {
        private readonly Dictionary<string, string> _local = new();
        private readonly Dictionary<string, string> _session = new();

        public List<KeyValuePair<string, string>> Writes { get; } = new();
        public bool? LastBootFlagPersist { get; private set; }

        public string? Local(string key) => _local.GetValueOrDefault(key);
        public string? Session(string key) => _session.GetValueOrDefault(key);
        public void SetLocal(string key, string value) => _local[key] = value;
        public void SetSession(string key, string value) => _session[key] = value;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem": result = _local.GetValueOrDefault((string)args![0]!); break;
                case "sessionStorage.getItem": result = _session.GetValueOrDefault((string)args![0]!); break;
                case "localStorage.setItem": Write(_local, args!); break;
                case "sessionStorage.setItem": Write(_session, args!); break;
                case "localStorage.removeItem": _local.Remove((string)args![0]!); break;
                case "sessionStorage.removeItem": _session.Remove((string)args![0]!); break;
                case "studylifeGetBootMarks": LastBootFlagPersist = (bool)args![0]!; break;
            }
            return ValueTask.FromResult(result is TValue typed ? typed : default!);
        }

        private void Write(Dictionary<string, string> target, object?[] args)
        {
            var key = (string)args[0]!;
            var value = (string)args[1]!;
            target[key] = value;
            Writes.Add(new(key, value));
        }
    }
}
