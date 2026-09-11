using StudyLife.Client.Services;

namespace StudyLife.Shared.Tests;

/// <summary>
/// LocalDate (src/StudyLife.Client/Services/LocalDate.cs, compiled into this project via a
/// &lt;Compile Include&gt; link). The client runs with InvariantGlobalization, so these patterns and
/// the invariant fallback names are the ONLY thing standing between the user and US-formatted
/// dates in all 26 languages - hence tested here rather than left to manual clicking.
/// Static state is reset in the constructor: xunit runs the methods of one class sequentially,
/// and no other test class touches LocalDate.
/// </summary>
public class LocalDateTests
{
    private static readonly DateTime Sample = new(2026, 8, 30, 14, 5, 0); // a Sunday

    public LocalDateTests()
    {
        LocalDate.Language = "de";
        LocalDate.ResetNames();
    }

    [Theory]
    [InlineData("de", "30.08.2026")]
    [InlineData("en", "30/08/2026")]
    [InlineData("nl", "30-08-2026")]
    [InlineData("sv", "2026-08-30")]
    [InlineData("hu", "2026. 08. 30.")]
    [InlineData("ru", "30.08.2026")]
    public void Short_UsesThePatternOfTheActiveLanguage(string language, string expected)
    {
        LocalDate.Language = language;
        Assert.Equal(expected, LocalDate.Short(Sample));
    }

    [Fact]
    public void Short_AcceptsARegionQualifiedLanguageTag()
    {
        LocalDate.Language = "en-GB";
        Assert.Equal("30/08/2026", LocalDate.Short(Sample));
    }

    [Theory]
    [InlineData("de", "30.08.26")]
    [InlineData("en", "30/08/26")]
    [InlineData("sv", "26-08-30")]
    public void ShortYear_ShortensTheYearInPlace(string language, string expected)
    {
        LocalDate.Language = language;
        Assert.Equal(expected, LocalDate.ShortYear(Sample));
    }

    [Theory]
    [InlineData("de", "30.08.")]
    [InlineData("en", "30/08")]
    [InlineData("nl", "30-08")]
    [InlineData("sv", "08-30")]
    [InlineData("hu", "08. 30.")]
    public void DayMonth_DropsTheYear(string language, string expected)
    {
        LocalDate.Language = language;
        Assert.Equal(expected, LocalDate.DayMonth(Sample));
    }

    [Theory]
    [InlineData("de", "14:05")]
    [InlineData("fr", "14:05")]
    [InlineData("en", "2:05 PM")]
    public void Time_Is24hEverywhereExceptEnglish(string language, string expected)
    {
        LocalDate.Language = language;
        Assert.Equal(expected, LocalDate.Time(Sample));
    }

    [Fact]
    public void DateAndTime_CombinesDateAndTime()
    {
        LocalDate.Language = "de";
        Assert.Equal("30.08.2026 14:05", LocalDate.DateAndTime(Sample));
    }

    [Fact]
    public void Names_FallBackToInvariantEnglishBeforeAnythingIsLoaded()
    {
        LocalDate.Language = "de";
        Assert.Equal("Sun", LocalDate.Weekday(Sample, true));
        Assert.Equal("Sunday", LocalDate.Weekday(Sample, false));
        Assert.Equal("Aug", LocalDate.MonthName(Sample, true));
        Assert.Equal("August", LocalDate.MonthName(Sample, false));
    }

    [Fact]
    public void WeekdayArraysAreIndexedByDayOfWeek()
    {
        Assert.Equal("Sun", LocalDate.Weekday(DayOfWeek.Sunday, true));
        Assert.Equal("Sat", LocalDate.Weekday(DayOfWeek.Saturday, true));
    }

    [Fact]
    public void MonthNameTakesAOneBasedMonthNumber()
    {
        Assert.Equal("Jan", LocalDate.MonthName(1, true));
        Assert.Equal("December", LocalDate.MonthName(12, false));
    }

    [Fact]
    public void ApplyNames_InstallsTheLoadedNamesAndRaisesTheChangeEvent()
    {
        var raised = 0;
        void Handler() => raised++;
        LocalDate.NamesChanged += Handler;
        try
        {
            var german = GermanNames();
            LocalDate.ApplyNames("de", german);

            Assert.Equal("de", LocalDate.Language);
            Assert.Equal("So", LocalDate.Weekday(Sample, true));
            Assert.Equal("Sonntag", LocalDate.Weekday(Sample, false));
            Assert.Equal("Aug", LocalDate.MonthName(Sample, true));
            Assert.Equal("August", LocalDate.MonthName(Sample, false));
            Assert.Equal(1, raised);

            // Re-applying the very same instance (the loader's per-language cache) must not
            // trigger another render pass.
            LocalDate.ApplyNames("de", german);
            Assert.Equal(1, raised);
        }
        finally
        {
            LocalDate.NamesChanged -= Handler;
        }
    }

    public static TheoryData<DateNames?> MalformedNames() => new()
    {
        null,
        new DateNames([], [], [], []),
        new DateNames(["Sun"], ["Sunday"], ["Jan"], ["January"]),
        // A locale the browser doesn't know can yield blanks - must not reach the markup.
        new DateNames(
            ["", "", "", "", "", "", ""],
            ["a", "b", "c", "d", "e", "f", "g"],
            ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"],
            ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"]),
    };

    [Theory]
    [MemberData(nameof(MalformedNames))]
    public void ApplyNames_KeepsTheInvariantNamesWhenTheLoadedSetIsIncomplete(DateNames? names)
    {
        LocalDate.ApplyNames("xx", names);

        Assert.Equal("xx", LocalDate.Language);
        Assert.Equal("Sun", LocalDate.Weekday(Sample, true));
        Assert.Equal("August", LocalDate.MonthName(Sample, false));
    }

    [Fact]
    public void ApplyNames_LaterLanguageSwitchReplacesTheNames()
    {
        LocalDate.ApplyNames("de", GermanNames());
        LocalDate.ApplyNames("fr", FrenchNames());

        Assert.Equal("dim.", LocalDate.Weekday(Sample, true));
        Assert.Equal("août", LocalDate.MonthName(Sample, true));
        Assert.Equal("30/08/2026", LocalDate.Short(Sample));
    }

    private static DateNames GermanNames() => new(
        ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"],
        ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"],
        ["Jan", "Feb", "Mrz", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez"],
        ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"]);

    private static DateNames FrenchNames() => new(
        ["dim.", "lun.", "mar.", "mer.", "jeu.", "ven.", "sam."],
        ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"],
        ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc."],
        ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"]);
}
