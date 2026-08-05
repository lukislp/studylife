using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for ServerTimerModes.Resolve (no host needed - static, stateless). The
/// resolution/clamping rules are deliberately duplicated from the client's CustomTimerModes.Parse
/// (see the comment in ServerTimerModes.cs), so these tests pin the server copy to that contract:
/// built-ins win, custom modes come from the settings JSON (web casing), values are clamped to
/// the same ranges as in the client, and any malformed input degrades to null instead of throwing.
/// </summary>
public class ServerTimerModesTests
{
    [Fact]
    public void Resolve_BuiltInId_ReturnsBuiltInModeWithoutTouchingCustomJson()
    {
        // Even syntactically broken custom JSON must not matter for a built-in id.
        var mode = ServerTimerModes.Resolve(1, "{not json");

        Assert.NotNull(mode);
        Assert.Equal("Pomodoro Classic", mode!.Name);
        Assert.Equal(25, mode.FocusMinutes);
        Assert.Equal(5, mode.BreakMinutes);
        Assert.Equal(4, mode.Rounds);
    }

    [Fact]
    public void Resolve_UnknownIdWithNoCustomJson_ReturnsNull()
    {
        Assert.Null(ServerTimerModes.Resolve(999, null));
        Assert.Null(ServerTimerModes.Resolve(999, ""));
        Assert.Null(ServerTimerModes.Resolve(999, "   "));
    }

    [Fact]
    public void Resolve_CustomModeInWebCasedJson_ReturnsItsValues()
    {
        // JsonSerializerDefaults.Web: camelCase property names, as the client serializes them.
        var json = """[{"id":100,"name":"My Mode","focusMinutes":45,"breakMinutes":15,"rounds":2}]""";

        var mode = ServerTimerModes.Resolve(100, json);

        Assert.NotNull(mode);
        Assert.Equal(100, mode!.Id);
        Assert.Equal("My Mode", mode.Name);
        Assert.Equal(45, mode.FocusMinutes);
        Assert.Equal(15, mode.BreakMinutes);
        Assert.Equal(2, mode.Rounds);
    }

    [Fact]
    public void Resolve_CustomModeOutOfRangeValues_AreClampedToClientRules()
    {
        // Same clamps as CustomTimerModes.Parse: focus 5..180, break 0..60, rounds 1..10.
        var json = """[{"id":100,"name":"Extreme","focusMinutes":999,"breakMinutes":-5,"rounds":0},"""
            + """{"id":101,"name":"Tiny","focusMinutes":1,"breakMinutes":90,"rounds":99}]""";

        var extreme = ServerTimerModes.Resolve(100, json);
        var tiny = ServerTimerModes.Resolve(101, json);

        Assert.Equal((180, 0, 1), (extreme!.FocusMinutes, extreme.BreakMinutes, extreme.Rounds));
        Assert.Equal((5, 60, 10), (tiny!.FocusMinutes, tiny.BreakMinutes, tiny.Rounds));
    }

    [Fact]
    public void Resolve_CustomIdBelow100_IsRejectedEvenIfPresentInJson()
    {
        // The custom id scheme starts at 100 - ids below that in the JSON must never shadow
        // (or extend) the built-in range.
        var json = """[{"id":42,"name":"Impostor","focusMinutes":30,"breakMinutes":5,"rounds":3}]""";

        Assert.Null(ServerTimerModes.Resolve(42, json));
    }

    [Fact]
    public void Resolve_CustomModeWithBlankName_IsRejected()
    {
        var json = """[{"id":100,"name":"  ","focusMinutes":30,"breakMinutes":5,"rounds":3}]""";

        Assert.Null(ServerTimerModes.Resolve(100, json));
    }

    [Fact]
    public void Resolve_IdNotInCustomList_ReturnsNull()
    {
        var json = """[{"id":100,"name":"My Mode","focusMinutes":45,"breakMinutes":15,"rounds":2}]""";

        Assert.Null(ServerTimerModes.Resolve(101, json));
    }

    [Fact]
    public void Resolve_MalformedCustomJson_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(ServerTimerModes.Resolve(100, "{definitely not json"));
    }

    [Fact]
    public void Resolve_JsonNullLiteral_ReturnsNullInsteadOfThrowing()
    {
        // Deserialize returns null here - the "?? new()" fallback must catch it.
        Assert.Null(ServerTimerModes.Resolve(100, "null"));
    }

    [Fact]
    public void Resolve_AllNineBuiltIns_AreResolvable()
    {
        for (var id = 1; id <= 9; id++)
        {
            var mode = ServerTimerModes.Resolve(id, null);
            Assert.NotNull(mode);
            Assert.Equal(id, mode!.Id);
            Assert.False(string.IsNullOrWhiteSpace(mode.Name));
        }
    }
}
