// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services;

/// <summary>
/// Sane ranges for the client-supplied timer values the server stores and the Live Activity
/// worker later steps through (audit L12). A round count or phase end outside them is clamped,
/// not rejected: the timer push is fire-and-forget and a 400 would only break it for no one.
/// </summary>
public static class TimerStateLimits
{
    /// <summary>Far above any real timer mode (rounds are single digits).</summary>
    public const int MaxRound = 1000;

    /// <summary>A phase end this far in the past or future is not a real timer phase.</summary>
    public static readonly TimeSpan MaxPhaseOffset = TimeSpan.FromDays(2);

    /// <summary>Upper bound of phases the worker steps through in one tick.</summary>
    public const int MaxCatchUpPhases = 2 * MaxRound + 2;

    public static int ClampRound(int round) => Math.Clamp(round, 0, MaxRound);

    public static DateTime? ClampPhaseEnd(DateTime? phaseEnd, DateTime now) =>
        phaseEnd is { } end ? new DateTime(Math.Clamp(end.Ticks, (now - MaxPhaseOffset).Ticks, (now + MaxPhaseOffset).Ticks), end.Kind) : null;
}
