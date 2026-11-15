// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>What a key press or a horizontal gesture on the calendar means.</summary>
public enum CalendarNavStep { None, Previous, Next, Today }

/// <summary>
/// The pure decision behind keyboard and trackpad navigation on the calendar page: which key or
/// gesture moves to which period. It knows nothing about the DOM - the collocated
/// Calendar.razor.js reports the facts (key, modifiers, whether focus is in a text field or a
/// dialog is open, accumulated wheel deltas) and <see cref="Pages.Calendar"/> applies the answer.
/// The period itself (week or day) is the page's business: Previous/Next move one week in the
/// week view and one day in the day view.
/// </summary>
public static class CalendarNavigation
{
    /// <summary>Accumulated horizontal wheel/trackpad delta (px) that counts as one step.</summary>
    public const double WheelThreshold = 80;

    /// <summary>The horizontal delta must exceed the vertical one by this factor, so a diagonal
    /// or mostly vertical scroll never turns pages.</summary>
    public const double WheelAxisRatio = 1.5;

    /// <param name="key">KeyboardEvent.key.</param>
    /// <param name="blocked">Focus is in a text input/textarea/select/contenteditable, or a
    /// dialog is open - the key belongs to that context.</param>
    public static CalendarNavStep FromKey(string? key, bool ctrl, bool meta, bool alt, bool shift, bool blocked)
    {
        // Cmd/Ctrl/Alt combinations stay with the browser and the OS (Cmd+Left is "back"),
        // Shift is left free for future selection shortcuts.
        if (blocked || ctrl || meta || alt || shift) return CalendarNavStep.None;
        return key switch
        {
            "ArrowLeft" => CalendarNavStep.Previous,
            "ArrowRight" => CalendarNavStep.Next,
            "t" or "T" => CalendarNavStep.Today,
            _ => CalendarNavStep.None,
        };
    }

    /// <summary>A completed horizontal swipe, either as pointer travel or as accumulated wheel
    /// delta. Content moves with the finger (swipe right = earlier), while a positive wheel deltaX
    /// means "scroll right" (= later); the caller passes <paramref name="dx"/> already as
    /// "positive = towards later".</summary>
    public static CalendarNavStep FromHorizontalGesture(double dx, double dy)
    {
        if (Math.Abs(dx) < WheelThreshold) return CalendarNavStep.None;
        if (Math.Abs(dx) <= Math.Abs(dy) * WheelAxisRatio) return CalendarNavStep.None;
        return dx > 0 ? CalendarNavStep.Next : CalendarNavStep.Previous;
    }
}
