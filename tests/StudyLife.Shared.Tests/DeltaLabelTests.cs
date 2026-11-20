// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>A change that is exactly level is no trend: no label, so no arrow with "0h" is shown.</summary>
public class DeltaLabelTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.004)]
    [InlineData(-0.004)]
    public void A_level_change_has_no_label(double delta) => Assert.Null(StudyMetrics.FormatDeltaHours(delta));

    [Theory]
    [InlineData(2.5, "2h 30m")]
    [InlineData(-2.5, "2h 30m")]
    [InlineData(3.0, "3h")]
    public void A_change_is_labelled_without_a_sign(double delta, string expected) =>
        Assert.Equal(expected, StudyMetrics.FormatDeltaHours(delta));
}
