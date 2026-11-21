// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;
using Xunit;

namespace StudyLife.Shared.Tests;

public class FormatGradeTests
{
    [Theory]
    [InlineData(1.7, "1,70")]
    [InlineData(2.0, "2,00")]
    [InlineData(1.0, "1,00")]
    public void FormatGrade_UsesCommaSeparator(decimal grade, string expected)
        => Assert.Equal(expected, StudyMetrics.FormatGrade(grade));

    [Theory]
    [InlineData(1.7, "0.0", "1,7")]
    [InlineData(2.0, "0.0", "2,0")]
    [InlineData(1.75, "0.0", "1,8")]
    [InlineData(1.7, "0.00", "1,70")]
    public void FormatGrade_WithFormat_UsesCommaSeparator(decimal grade, string format, string expected)
        => Assert.Equal(expected, StudyMetrics.FormatGrade(grade, format));

    [Theory]
    [InlineData(11.06, "0.0", "11,1")]
    [InlineData(31.44, "0.0", "31,4")]
    [InlineData(3.857, "0.#", "3,9")]
    [InlineData(4.0, "0.#", "4")]
    public void FormatDecimal_UsesTheSameCommaConventionAsGrades(double value, string format, string expected)
        => Assert.Equal(expected, StudyMetrics.FormatDecimal(value, format));
}
