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
}
