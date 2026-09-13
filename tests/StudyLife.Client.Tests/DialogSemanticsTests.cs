using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using StudyLife.Client.Components.Setup;

namespace StudyLife.Client.Tests;

/// <summary>
/// Accessibility contract of the modal markup, checked against two representative components:
/// SetupCompletionModal (always-open modal) and SetupCalendarSubscriptionCard (modal behind a
/// confirmation step). Both take their texts as plain parameters, so they render without the
/// I18nText table loader and stand in for the ~27 dialogs that share this markup shape.
/// </summary>
public class DialogSemanticsTests : BunitContext
{
    private const string CloseLabel = "Close dialog";

    private IElement RenderCompletionModal() =>
        Render<SetupCompletionModal>(p => p
            .Add(c => c.CourseName, "Algorithms")
            .Add(c => c.TitleFormat, "{0} completed")
            .Add(c => c.CloseLabel, CloseLabel)).Find("[role=dialog]");

    private IElement RenderCalendarSubscriptionModal()
    {
        var cut = Render<SetupCalendarSubscriptionCard>(p => p
            .Add(c => c.Label, "Calendar subscription")
            .Add(c => c.RegenerateButtonText, "Regenerate")
            .Add(c => c.RegenerateConfirmTitle, "Regenerate calendar link?")
            .Add(c => c.CloseLabel, CloseLabel));
        cut.Find("button.btn-danger").Click(); // opens the confirmation dialog
        return cut.Find("[role=dialog]");
    }

    public static TheoryData<string> Components => new() { "completion", "calendar-subscription" };

    private IElement Dialog(string which) =>
        which == "completion" ? RenderCompletionModal() : RenderCalendarSubscriptionModal();

    [Theory]
    [MemberData(nameof(Components))]
    public void ModalContainer_IsAModalDialog(string which)
    {
        var dialog = Dialog(which);

        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Contains("modal", dialog.ClassList);
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void AriaLabelledBy_ResolvesToANonEmptyTitleElement(string which)
    {
        var dialog = Dialog(which);

        var labelledBy = dialog.GetAttribute("aria-labelledby");
        Assert.False(string.IsNullOrWhiteSpace(labelledBy));

        var title = RootOf(dialog).QuerySelector($"[id=\"{labelledBy}\"]");
        Assert.NotNull(title);
        Assert.False(string.IsNullOrWhiteSpace(title!.TextContent));
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void EveryIconOnlyButton_HasAnAriaLabel(string which)
    {
        var dialog = Dialog(which);

        var iconOnly = dialog.QuerySelectorAll("button")
            .Where(b => IsIconOnly(b.TextContent))
            .ToList();

        Assert.NotEmpty(iconOnly);
        foreach (var button in iconOnly)
            Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")),
                $"icon-only button '{button.TextContent.Trim()}' has no aria-label");
    }

    /// <summary>Walks up to the top of the rendered fragment, so the id lookup covers the whole
    /// markup the component produced - not just the dialog's own subtree.</summary>
    private static IElement RootOf(IElement element)
    {
        var current = element;
        while (current.ParentElement is { } parent) current = parent;
        return current;
    }

    /// <summary>A button is icon-only when its visible content carries no letters or digits at
    /// all - i.e. nothing a screen reader could announce as a name.</summary>
    private static bool IsIconOnly(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && !Regex.IsMatch(trimmed, "[A-Za-z0-9]");
    }
}
