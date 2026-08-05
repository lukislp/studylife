using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StudyLife.Server.Tests;

/// <summary>
/// Success path of POST /api/backup/restore/restart: with a STAGED restore the endpoint
/// answers 202 and then actually shuts the host down (after a short delay so the response
/// still reaches the client). Deliberately NO IClassFixture and a factory instance created
/// inside the test: the endpoint stops the application host, so this factory is sacrificial -
/// no other test class shares it, and BackupRestoreTests deliberately skips exactly this case
/// for that reason.
/// </summary>
public class BackupControllerRestartTests
{
    [Fact]
    public async Task RestartToApply_WithStagedRestore_Returns202AndStopsTheHost()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Stage a restore first (the 409 guard otherwise refuses, covered in BackupRestoreTests):
        // pull a real backup via the download endpoint and upload it back.
        var backupBytes = await (await client.GetAsync("/api/backup/database")).Content.ReadAsByteArrayAsync();
        using (var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(backupBytes), "file", "studylife-backup.db" },
        })
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/backup/restore", upload)).StatusCode);
        }

        // Observe the REAL shutdown instead of sleeping blindly: StopApplication fires
        // ApplicationStopping, ~750ms after the response.
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        var response = await client.PostAsync("/api/backup/restore/restart", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("restarting", body.RootElement.GetProperty("status").GetString());
            // The message documents the manual-restart fallback for deployments without an
            // auto-restart policy - part of the endpoint's contract (see controller comment).
            Assert.Contains("auto-restart", body.RootElement.GetProperty("message").GetString());
        }

        // The host really shuts down shortly after (the actual point of the endpoint).
        var completed = await Task.WhenAny(stopping.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == stopping.Task, "Host did not begin shutting down after restore/restart");
    }
}
