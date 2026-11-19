// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using StudyLife.Server.Diagnostics;

namespace StudyLife.Server.Tests;

/// <summary>Audit 2026-11-19 S10(a): outgoing URLs (web-push endpoints, APNs device tokens) stay out of traces.</summary>
public class OutgoingUrlRedactionProcessorTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/dXNlci1kZXZpY2UtdG9rZW4", "https://fcm.googleapis.com/{redacted}")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAAAB-secret?x=1", "https://updates.push.services.mozilla.com/{redacted}")]
    [InlineData("https://api.push.apple.com/3/device/00fc13adff785122b4ad28809a3420982341241421348097878e577c991de8f0", "https://api.push.apple.com/{redacted}")]
    [InlineData("https://api.push.apple.com:443/3/device/abc", "https://api.push.apple.com/{redacted}")]
    [InlineData("https://webhooks.internal:8443/internal/events", "https://webhooks.internal:8443/internal/events")]
    [InlineData("https://webhooks.internal/internal/webhooks?user_id=7", "https://webhooks.internal/internal/webhooks")]
    public void Only_the_host_survives_except_for_internal_add_on_calls(string url, string expected) =>
        Assert.Equal(expected, OutgoingUrlRedactionProcessor.Redact(url));

    [Fact]
    public void A_value_that_is_not_an_absolute_url_is_dropped() =>
        Assert.Null(OutgoingUrlRedactionProcessor.Redact("/3/device/abc"));

    private static Activity ClientSpan(ActivityKind kind)
    {
        using var source = new ActivitySource("test-outgoing-redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-outgoing-redaction",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var activity = source.StartActivity("POST", kind)!;
        activity.SetTag("url.full", "https://api.push.apple.com/3/device/secret-device-token");
        activity.SetTag("http.url", "https://api.push.apple.com/3/device/secret-device-token");
        activity.SetTag("url.query", "token=secret");
        activity.SetTag("server.address", "api.push.apple.com");
        return activity;
    }

    [Fact]
    public void A_finished_client_span_carries_no_device_token()
    {
        var activity = ClientSpan(ActivityKind.Client);

        new OutgoingUrlRedactionProcessor().OnEnd(activity);

        Assert.Equal("https://api.push.apple.com/{redacted}", activity.GetTagItem("url.full"));
        Assert.Equal("https://api.push.apple.com/{redacted}", activity.GetTagItem("http.url"));
        Assert.Null(activity.GetTagItem("url.query"));
        Assert.Equal("api.push.apple.com", activity.GetTagItem("server.address"));
        Assert.DoesNotContain(activity.TagObjects, t => t.Value?.ToString()?.Contains("secret") == true);
    }

    [Fact]
    public void Server_spans_are_left_alone()
    {
        var activity = ClientSpan(ActivityKind.Server);

        new OutgoingUrlRedactionProcessor().OnEnd(activity);

        Assert.Contains("secret-device-token", (string)activity.GetTagItem("url.full")!);
    }
}
