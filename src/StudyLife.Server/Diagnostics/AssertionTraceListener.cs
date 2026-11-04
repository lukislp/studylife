// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;

namespace StudyLife.Server.Diagnostics;

/// <summary>
/// Makes the run-time assertions (<see cref="Debug.Assert(bool, string)"/>) observable while the
/// server runs as a whole, not only inside a debugger. Only builds with the DEBUG symbol
/// (Configuration=Debug) compile <c>Debug.Assert</c> calls at all - the Release image ships
/// without them, so production behaviour and performance are untouched.
///
/// The .NET default for a failed assertion without an attached debugger is
/// <c>Environment.FailFast</c>: the process dies on the first hit. That is useless under a
/// dynamic scan (docs/SECURITY-ASSURANCE.md 8.5, the "assertions" leg of
/// .github/workflows/dast.yml), which should see every violated invariant of a run, not just
/// the first one. This listener replaces the default one and writes each failure as one
/// greppable line (<see cref="FailureMarker"/>) plus the stack trace to stderr instead; the
/// workflow fails the job if the container log contains the marker.
/// </summary>
internal sealed class AssertionTraceListener : TraceListener
{
    /// <summary>Prefix of every failed-assertion line; the DAST workflow greps for it.</summary>
    public const string FailureMarker = "STUDYLIFE-ASSERTION-FAILED";

    /// <summary>Logged once at startup when assertions are compiled in, so the workflow can
    /// prove it scanned an assertion-enabled build and not a Release image by mistake.</summary>
    public const string EnabledMarker = "STUDYLIFE-ASSERTIONS-ENABLED";

    private static readonly Lock WriteLock = new();

    private readonly TextWriter? _output;

    /// <param name="output">Where failures go; null means the process's stderr (the
    /// parameter exists for tests).</param>
    public AssertionTraceListener(TextWriter? output = null) => _output = output;

    /// <summary>Routes Debug.Assert failures to this listener. The call itself is compiled
    /// away in Release builds.</summary>
    [Conditional("DEBUG")]
    public static void Install()
    {
        // Touching Trace.Listeners also switches Debug's provider over to the trace listeners,
        // so Debug.Assert/Debug.Fail end up in Fail() below instead of failing fast.
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new AssertionTraceListener());
        // stdout, not stderr: the build-time OpenAPI generator (dotnet-getdocument) starts this
        // entry point too and treats any stderr output as a build error.
        Console.Out.WriteLine($"{EnabledMarker}: failed Debug.Assert calls are reported on stderr");
    }

    public override void Fail(string? message, string? detailMessage)
    {
        var text = string.IsNullOrEmpty(detailMessage) ? message : $"{message} - {detailMessage}";
        var output = _output ?? Console.Error;
        lock (WriteLock)
        {
            output.WriteLine($"{FailureMarker}: {text}");
            output.WriteLine(new StackTrace(1, fNeedFileInfo: true).ToString());
            output.Flush();
        }
    }

    // Debug.WriteLine/Trace.WriteLine output is not used by the server; the console logger is
    // the only log channel, so plain writes are dropped instead of duplicating it.
    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
    }
}
