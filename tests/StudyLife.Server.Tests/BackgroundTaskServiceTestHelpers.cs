using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// BackgroundTaskService is deliberately removed as an IHostedService in CustomWebApplicationFactory
/// (see the comment there), so the 30s poller doesn't run uncontrolled against the test DB.
/// For targeted tests of the individual Run*Async methods (now internal instead of private, see
/// InternalsVisibleTo in StudyLife.Server.csproj) a dedicated instance is constructed directly per
/// test class instead - the constructor dependencies come unchanged from factory.Services,
/// exactly the same ones Program.cs uses for the real registration.
/// </summary>
internal static class BackgroundTaskServiceTestFactory
{
    /// <summary>apnsSender override for tests that need an actually "enabled" sender with a
    /// stub HTTP handler (Live Activity push, see LiveActivityPushTests) - the
    /// DI-registered sender in the test host has no Apns:* config and is therefore Enabled=false.</summary>
    public static BackgroundTaskService Create(CustomWebApplicationFactory factory, ApnsSender? apnsSender = null) => new(
        factory.Services,
        factory.Services.GetRequiredService<VapidKeysHolder>(),
        factory.Services.GetRequiredService<ILogger<BackgroundTaskService>>(),
        apnsSender ?? factory.Services.GetRequiredService<ApnsSender>(),
        backupService: factory.Services.GetRequiredService<DatabaseBackupService>());
}

/// <summary>
/// Generates a cryptographically valid P-256 key pair in uncompressed point format, the way a
/// real browser would return it for PushSubscription.getKey('p256dh'). WebPushClient
/// encrypts the payload via ECDH with this key before any HTTP request even goes out -
/// a short placeholder string (as PushControllerTests uses for pure persistence tests)
/// would already fail during encryption and would never reach the real send path (including
/// 410 handling). Verified in a scratch program against a local HttpListener.
/// </summary>
internal static class FakePushKeys
{
    public static (string P256dh, string Auth) Generate()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(false);
        var uncompressed = new byte[65];
        uncompressed[0] = 0x04;
        Buffer.BlockCopy(parameters.Q.X!, 0, uncompressed, 1, 32);
        Buffer.BlockCopy(parameters.Q.Y!, 0, uncompressed, 33, 32);
        var auth = RandomNumberGenerator.GetBytes(16);
        return (Base64Url(uncompressed), Base64Url(auth));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// Minimal local HTTP server that answers every request with 410 Gone - simulates a
/// browser that has revoked a push subscription, without any real network access or a
/// mocking library (deliberately absent from the server test project, see
/// StudyLife.Server.Tests.csproj - no new dependency just for this one case).
/// </summary>
internal sealed class GoneEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    public string Url { get; }

    public GoneEndpoint()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Url = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = ServeLoopAsync();
    }

    private async Task ServeLoopAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                ctx.Response.StatusCode = 410;
                ctx.Response.Close();
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { /* best effort */ }
        try { _listener.Close(); } catch (Exception) { /* best effort */ }
    }
}

/// <summary>Shared test helper for Settings PUT (full object upsert, the way the real client sends it).</summary>
internal static class BackgroundTaskTestSettings
{
    public static async Task PutAsync(HttpClient client, Action<UserSettingsDto> configure)
    {
        var dto = new UserSettingsDto();
        configure(dto);
        var response = await client.PutAsJsonAsync("/api/settings", dto);
        response.EnsureSuccessStatusCode();
    }
}
