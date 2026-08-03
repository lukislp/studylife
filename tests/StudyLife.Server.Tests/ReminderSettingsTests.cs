using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

public class ParseSessionReminderMinutesTests
{
    [Fact]
    public void Null_ReturnsDefaults()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes(null);

        Assert.Equal(ReminderSettings.DefaultSessionReminderMinutes, result);
    }

    [Fact]
    public void EmptyString_ReturnsDefaults()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("");

        Assert.Equal(ReminderSettings.DefaultSessionReminderMinutes, result);
    }

    [Fact]
    public void WhitespaceOnly_ReturnsDefaults()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("   ");

        Assert.Equal(ReminderSettings.DefaultSessionReminderMinutes, result);
    }

    [Fact]
    public void SingleValue_ParsesToOneElementArray()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("15");

        Assert.Equal(new[] { 15 }, result);
    }

    [Fact]
    public void ValidCommaSeparatedList_ParsesInOrder()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("10,5,1");

        Assert.Equal(new[] { 10, 5, 1 }, result);
    }

    [Fact]
    public void ExtraWhitespaceAndEmptyEntries_AreTrimmedAndRemoved()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes(" 10 , , 5 ,3,");

        Assert.Equal(new[] { 10, 5, 3 }, result);
    }

    [Fact]
    public void MixOfValidAndNonNumericEntries_SilentlySkipsTheInvalidOnes()
    {
        // Parse doesn't throw and doesn't fall back to defaults just because *some* entries
        // are malformed - it filters them out via int.TryParse and keeps whatever parsed.
        var result = ReminderSettings.ParseSessionReminderMinutes("10,abc,5,,xyz,3");

        Assert.Equal(new[] { 10, 5, 3 }, result);
    }

    [Fact]
    public void AllEntriesNonNumeric_FallsBackToDefaults()
    {
        // Every item fails to parse -> values.Length == 0 -> fallback kicks in.
        var result = ReminderSettings.ParseSessionReminderMinutes("abc,def,xyz");

        Assert.Equal(ReminderSettings.DefaultSessionReminderMinutes, result);
    }

    [Fact]
    public void NegativeAndZeroValues_AreAcceptedAsIs_NoRangeValidation()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("-5,0,10");

        Assert.Equal(new[] { -5, 0, 10 }, result);
    }

    [Fact]
    public void DuplicateValues_AreKeptAsIs_NoDeduplication()
    {
        var result = ReminderSettings.ParseSessionReminderMinutes("5,5,5");

        Assert.Equal(new[] { 5, 5, 5 }, result);
    }
}

public class ParseCourseGoalReminderDaysTests
{
    [Fact]
    public void Null_ReturnsDefaults()
    {
        var result = ReminderSettings.ParseCourseGoalReminderDays(null);

        Assert.Equal(ReminderSettings.DefaultCourseGoalReminderDays, result);
    }

    [Fact]
    public void EmptyString_ReturnsDefaults()
    {
        var result = ReminderSettings.ParseCourseGoalReminderDays("");

        Assert.Equal(ReminderSettings.DefaultCourseGoalReminderDays, result);
    }

    [Fact]
    public void ValidList_ParsesCorrectly()
    {
        var result = ReminderSettings.ParseCourseGoalReminderDays("14,7,0");

        Assert.Equal(new[] { 14, 7, 0 }, result);
    }

    [Fact]
    public void MalformedEntries_AreSkippedWithoutThrowing()
    {
        var result = ReminderSettings.ParseCourseGoalReminderDays("14,seven,0");

        Assert.Equal(new[] { 14, 0 }, result);
    }

    [Fact]
    public void AllMalformed_FallsBackToDefaults()
    {
        var result = ReminderSettings.ParseCourseGoalReminderDays("not,a,number");

        Assert.Equal(ReminderSettings.DefaultCourseGoalReminderDays, result);
    }
}

public class GetInactivityThresholdDaysTests
{
    [Fact]
    public void PositiveValue_IsReturnedAsIs()
    {
        Assert.Equal(9, ReminderSettings.GetInactivityThresholdDays(9));
    }

    [Fact]
    public void Zero_FallsBackToDefault()
    {
        Assert.Equal(ReminderSettings.DefaultInactivityThresholdDays, ReminderSettings.GetInactivityThresholdDays(0));
    }

    [Fact]
    public void Negative_FallsBackToDefault()
    {
        Assert.Equal(ReminderSettings.DefaultInactivityThresholdDays, ReminderSettings.GetInactivityThresholdDays(-3));
    }

    [Fact]
    public void One_IsReturnedAsIs_NotTreatedAsInvalid()
    {
        Assert.Equal(1, ReminderSettings.GetInactivityThresholdDays(1));
    }
}
