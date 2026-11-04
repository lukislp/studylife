// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Diagnostics;

namespace StudyLife.Server.Tests;

/// <summary>
/// The assertions leg of .github/workflows/dast.yml fails exactly when the container log holds
/// a line starting with the failure marker - these tests pin that contract.
/// </summary>
public class AssertionTraceListenerTests
{
    [Fact]
    public void Fail_writes_one_marker_line_with_message_and_detail()
    {
        using var output = new StringWriter();
        using var listener = new AssertionTraceListener(output);

        listener.Fail("session window must be non-empty", "StartTime == EndTime");

        var lines = output.ToString().Split(Environment.NewLine);
        Assert.Equal(
            $"{AssertionTraceListener.FailureMarker}: session window must be non-empty - StartTime == EndTime",
            lines[0]);
        Assert.Single(lines, l => l.StartsWith(AssertionTraceListener.FailureMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void Fail_without_detail_writes_only_the_message()
    {
        using var output = new StringWriter();
        using var listener = new AssertionTraceListener(output);

        listener.Fail("tenant mismatch");

        Assert.StartsWith($"{AssertionTraceListener.FailureMarker}: tenant mismatch{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Plain_trace_writes_are_dropped()
    {
        using var output = new StringWriter();
        using var listener = new AssertionTraceListener(output);

        listener.Write("noise");
        listener.WriteLine("noise");

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Startup_marker_never_matches_the_failure_grep()
    {
        // The workflow greps the whole log for the failure marker; the startup line must not
        // count as a failure.
        Assert.DoesNotContain(AssertionTraceListener.FailureMarker, AssertionTraceListener.EnabledMarker, StringComparison.Ordinal);
    }
}
