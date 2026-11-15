// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class TelemetryErrorReportTests
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Aot_wasm_frames_without_file_and_line_are_kept()
    {
        // The shape the trimmed WebAssembly build prints: no " in File.cs:line" part at all.
        const string raw = "System.ArgumentNullException: Value cannot be null. (Parameter 'source')\n"
            + "   at System.Linq.ThrowHelper.ThrowArgumentNullException(ExceptionArgument argument)\n"
            + "   at System.Linq.Enumerable.Where[TSource](IEnumerable`1 source, Func`2 predicate)\n"
            + "   at StudyLife.Client.Pages.Planner.RecalcWeekHours()";

        var (stack, hash) = TelemetryService.SanitizeAndHashStack(raw);

        Assert.Equal(3, stack.Split('\n').Length);
        Assert.Contains("Planner.RecalcWeekHours()", stack);
        Assert.NotEqual(EmptyHash, hash);
    }

    [Fact]
    public void The_exception_message_never_leaves_the_device()
    {
        var (stack, _) = TelemetryService.SanitizeAndHashStack(
            "System.InvalidOperationException: secret user content: Note about exam\n   at A.B.C()");

        Assert.DoesNotContain("secret", stack);
        Assert.DoesNotContain("exam", stack);
    }

    [Fact]
    public void Query_strings_are_stripped_from_frames()
    {
        var (stack, _) = TelemetryService.SanitizeAndHashStack("E: m\n   at x (https://h/app.js?token=abc:1:2)");

        Assert.DoesNotContain("token", stack);
    }

    [Fact]
    public void A_missing_stack_still_hashes_to_the_empty_hash()
        => Assert.Equal(EmptyHash, TelemetryService.SanitizeAndHashStack(null).StackHash);

    [Theory]
    [InlineData("planner", "/planner")]
    [InlineData("planner?x=1", "/planner")]
    [InlineData("Planner/sub/path#frag", "/planner")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Page_is_the_first_route_segment_in_the_shape_the_server_accepts(string? relative, string expected)
        => Assert.Equal(expected, TelemetryService.PageFromRelativePath(relative));
}
