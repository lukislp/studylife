// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Texts;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>Captures every entry with its rendered message AND its structured properties, so a
/// test can prove that personal content is in neither (GDPR audit F-12).</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties)
    {
        /// <summary>Message plus every property value and exception text, for substring checks.</summary>
        public string Everything =>
            Message + " " + string.Join(" ", Properties.Select(p => $"{p.Key}={p.Value}"));
    }

    public List<Entry> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var props = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        var text = formatter(state, exception) + (exception is null ? "" : " " + exception);
        lock (Entries) Entries.Add(new Entry(logLevel, text, props));
    }

    /// <summary>Entries at Information and above - what a default production Loki sees.</summary>
    public IEnumerable<Entry> InformationAndAbove() => Entries.Where(e => e.Level >= LogLevel.Information);
}

/// <summary>The background push senders log ids and outcomes only: never a course name, a push
/// title/body or a Web Push endpoint URL at Information or above.</summary>
public class LogMinimisationTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string CourseName = "PrivateCourseNameXyz";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LogMinimisationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private BackgroundTaskService CreateService(CapturingLogger<BackgroundTaskService> logger) => new(
        _factory.Services,
        _factory.Services.GetRequiredService<VapidKeysHolder>(),
        logger,
        _factory.Services.GetRequiredService<ApnsSender>(),
        backupService: _factory.Services.GetRequiredService<DatabaseBackupService>(),
        timeProvider: new FixedTimeProvider(PinnedClock.Instant),
        webPush: _factory.Services.GetRequiredService<WebPushDelivery>());

    [Fact]
    public async Task StreakRiskPush_LogsKeyOnly_NoBodyNoCourseNoEndpoint()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s => s.StreakRiskRemindersEnabled = true);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);
        for (var daysAgo = 3; daysAgo >= 1; daysAgo--)
        {
            var start = PinnedClock.LocalDateTime.Date.AddDays(-daysAgo).AddHours(10);
            await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
            {
                CourseId = 1,
                CourseName = CourseName,
                CourseColor = "#6C5CE7",
                StartTime = start,
                EndTime = start.AddHours(1),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        var logger = new CapturingLogger<BackgroundTaskService>();
        var service = CreateService(logger);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunStreakRiskCheckAsync(db, () => db.PushSubscriptions.ToListAsync());

        var t = ServerText.For(new UserSettingsDto().Language);
        var body = t.T("streakRisk.body", 3);
        var visible = logger.InformationAndAbove().ToList();
        Assert.Contains(visible, e => e.Message.Contains("streakrisk:"));          // the outcome is still logged
        AssertNoPersonalContent(visible, CourseName, body, gone.Url);
    }

    [Fact]
    public async Task DailyMotivationPush_DoesNotLogTheBody()
    {
        await BackgroundTaskTestSettings.PutAsync(_client, s =>
        {
            s.DailyMotivationEnabled = true;
            s.MotivationalStyle = "hype";
        });
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        var logger = new CapturingLogger<BackgroundTaskService>();
        var service = CreateService(logger);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        await service.RunDailyMotivationAsync(db, () => db.PushSubscriptions.ToListAsync());

        var visible = logger.InformationAndAbove().ToList();
        Assert.Contains(visible, e => e.Message.Contains("dailymotivation:"));
        // Every motivation text of the style would be a leak; the endpoint is the sharper probe.
        AssertNoPersonalContent(visible, gone.Url);
        Assert.DoesNotContain(visible, e => e.Properties.Any(p => p.Key == "Body"));
    }

    [Fact]
    public async Task FailedPushDelivery_WarnsWithTheSubscriptionIdNotTheEndpoint()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var endpoint = $"http://127.0.0.1:{port}/secret-endpoint-token";
        var (p256dh, auth) = FakePushKeys.Generate();
        var sub = new PushSubscriptionEntity { Id = 4242, Endpoint = endpoint, P256dh = p256dh, Auth = auth, CreatedAt = DateTime.UtcNow };

        var logger = new CapturingLogger<BackgroundTaskService>();
        var service = CreateService(logger);
        await service.SendPushAsync(service.GetPushClient(), sub, "{}", "push failed for subscription {SubscriptionId}");

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Contains(warnings, e => e.Message.Contains("4242"));
        Assert.DoesNotContain(warnings, e => e.Everything.Contains("secret-endpoint-token"));
    }

    private static void AssertNoPersonalContent(IEnumerable<CapturingLogger<BackgroundTaskService>.Entry> entries, params string[] forbidden)
    {
        foreach (var entry in entries)
            foreach (var needle in forbidden)
                Assert.DoesNotContain(needle, entry.Everything);
    }
}

/// <summary>The setup code is delivered by a labelled stdout banner (the documented operator flow
/// is <c>docker compose logs | grep -i -A2 "setup code"</c>), never through ILogger.</summary>
public class SetupBannerTests
{
    [Fact]
    public void Banner_IsLabelled_AndShowsTheCodeExactlyOnce()
    {
        var writer = new StringWriter();
        SystemSecretsService.WriteSetupBanner(writer, "ABCD-EFGH-JKLM");

        var text = writer.ToString();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "setup code"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "ABCD-EFGH-JKLM"));
        // `grep -A2 "setup code"` must reach the code: label, blank line, code.
        var lines = text.Split(Environment.NewLine);
        var labelAt = Array.FindIndex(lines, l => l.Contains("setup code"));
        Assert.Contains("ABCD-EFGH-JKLM", lines[labelAt + 2]);
    }

    [Fact]
    public void Banner_WithNote_StillCarriesOneLabelAndOneCode()
    {
        var writer = new StringWriter();
        SystemSecretsService.WriteSetupBanner(writer, "ABCD-EFGH-JKLM", "The last account was deleted - this installation is back at first run.");

        var text = writer.ToString();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "setup code"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "ABCD-EFGH-JKLM"));
        Assert.Contains("back at first run", text);
    }
}
