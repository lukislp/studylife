using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcWeightedAverageGradeTests
{
    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        Assert.Null(StudyMetrics.CalcWeightedAverageGrade(Array.Empty<(decimal, int)>()));
    }

    [Fact]
    public void SingleCourse_ReturnsItsGrade()
    {
        var courses = new (decimal Grade, int Ects)[] { (2.0m, 5) };
        Assert.Equal(2.0m, StudyMetrics.CalcWeightedAverageGrade(courses));
    }

    [Fact]
    public void MultipleCourses_ReturnsEctsWeightedAverage()
    {
        // (1.0*5 + 3.0*10) / 15 = 35/15 = 2.333...
        var courses = new (decimal Grade, int Ects)[] { (1.0m, 5), (3.0m, 10) };
        var result = StudyMetrics.CalcWeightedAverageGrade(courses);

        Assert.NotNull(result);
        Assert.Equal(35.0m / 15.0m, result!.Value);
    }

    [Fact]
    public void ZeroTotalEcts_FallsBackToUnweightedAverage()
    {
        var courses = new (decimal Grade, int Ects)[] { (1.0m, 0), (3.0m, 0) };
        var result = StudyMetrics.CalcWeightedAverageGrade(courses);

        Assert.Equal(2.0m, result);
    }

    [Fact]
    public void MixOfZeroAndNonZeroEcts_UsesEctsWeightingWhenSumPositive()
    {
        // Sum of Ects = 5 (only the first course carries weight), so weighting path is used:
        // (1.0*5 + 4.0*0) / 5 = 1.0
        var courses = new (decimal Grade, int Ects)[] { (1.0m, 5), (4.0m, 0) };
        var result = StudyMetrics.CalcWeightedAverageGrade(courses);

        Assert.Equal(1.0m, result);
    }

    [Fact]
    public void AllSameGrade_ReturnsThatGradeRegardlessOfWeights()
    {
        var courses = new (decimal Grade, int Ects)[] { (2.5m, 5), (2.5m, 10), (2.5m, 30) };
        Assert.Equal(2.5m, StudyMetrics.CalcWeightedAverageGrade(courses));
    }
}
