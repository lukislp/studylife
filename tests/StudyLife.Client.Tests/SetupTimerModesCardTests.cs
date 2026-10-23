using Bunit;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>
/// The custom timer mode form must save exactly what was entered, or refuse visibly - never
/// silently clamp (a 1-minute focus used to be saved as 5 and shown as 5 on the Focus page).
/// </summary>
public class SetupTimerModesCardTests : BunitContext
{
    private const string RangeError = "Allowed: focus {0}-{1} min, break {2}-{3} min, rounds {4}-{5}.";

    private (IRenderedComponent<SetupTimerModesCard> Cut, List<TimerMode> Added) RenderCard()
    {
        var added = new List<TimerMode>();
        var cut = Render<SetupTimerModesCard>(p => p
            .Add(c => c.AddButtonText, "Add mode")
            .Add(c => c.RangeError, RangeError)
            .Add(c => c.OnAdd, (TimerMode m) => added.Add(m)));
        return (cut, added);
    }

    private static void Fill(IRenderedComponent<SetupTimerModesCard> cut, string name, int focus, int brk, int rounds)
    {
        cut.Find("input[maxlength]").Input(name);
        // Re-query after every change: each one re-renders and invalidates earlier handler ids.
        cut.FindAll("input[type=number]")[0].Change(focus.ToString());
        cut.FindAll("input[type=number]")[1].Change(brk.ToString());
        cut.FindAll("input[type=number]")[2].Change(rounds.ToString());
    }

    [Fact]
    public void Add_OneMinuteFocus_IsPassedOnUnchanged()
    {
        var (cut, added) = RenderCard();

        Fill(cut, "Quick test", 1, 2, 5);
        cut.Find("button.btn-secondary").Click();

        var mode = Assert.Single(added);
        Assert.Equal(("Quick test", 1, 2, 5), (mode.Name, mode.FocusMinutes, mode.BreakMinutes, mode.Rounds));
    }

    [Fact]
    public void Add_OneMinuteFocus_RoundTripsThroughStorageUnchanged()
    {
        var (cut, added) = RenderCard();
        Fill(cut, "Quick test", 1, 2, 5);
        cut.Find("button.btn-secondary").Click();
        var draft = Assert.Single(added);
        draft.Id = CustomTimerModes.NextId([]);

        // Same path as Setup.razor (serialize) -> Focus.razor (Combined).
        var json = CustomTimerModes.Serialize([draft]);
        var onFocusPage = CustomTimerModes.Combined(json).Single(m => m.Id == draft.Id);

        Assert.Equal((1, 2, 5), (onFocusPage.FocusMinutes, onFocusPage.BreakMinutes, onFocusPage.Rounds));
    }

    [Theory]
    [InlineData(0, 5, 4)]
    [InlineData(181, 5, 4)]
    [InlineData(25, 61, 4)]
    [InlineData(25, -1, 4)]
    [InlineData(25, 5, 0)]
    [InlineData(25, 5, 11)]
    public void Add_OutOfRange_IsRefusedWithVisibleError(int focus, int brk, int rounds)
    {
        var (cut, added) = RenderCard();

        Fill(cut, "Broken", focus, brk, rounds);
        cut.Find("button.btn-secondary").Click();

        Assert.Empty(added);
        var expected = string.Format(RangeError,
            TimerModeCatalog.MinFocusMinutes, TimerModeCatalog.MaxFocusMinutes,
            TimerModeCatalog.MinBreakMinutes, TimerModeCatalog.MaxBreakMinutes,
            TimerModeCatalog.MinRounds, TimerModeCatalog.MaxRounds);
        Assert.Contains(expected, cut.Markup);
    }

    [Fact]
    public void FocusInput_AdvertisesTheCatalogBounds()
    {
        var (cut, _) = RenderCard();

        var focus = cut.FindAll("input[type=number]")[0];

        Assert.Equal(TimerModeCatalog.MinFocusMinutes.ToString(), focus.GetAttribute("min"));
        Assert.Equal(TimerModeCatalog.MaxFocusMinutes.ToString(), focus.GetAttribute("max"));
    }
}
