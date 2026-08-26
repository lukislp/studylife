using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

public class CommaSeparatedIdsTests
{
    [Fact]
    public void Null_ReturnsEmptyList()
    {
        Assert.Empty(CommaSeparatedIds.Parse(null));
    }

    [Fact]
    public void EmptyString_ReturnsEmptyList()
    {
        Assert.Empty(CommaSeparatedIds.Parse(""));
    }

    [Fact]
    public void ValidCommaSeparatedList_ParsesInOrder()
    {
        Assert.Equal(new List<int> { 1, 2, 3 }, CommaSeparatedIds.Parse("1,2,3"));
    }

    [Fact]
    public void SingleValue_ParsesToOneElementList()
    {
        Assert.Equal(new List<int> { 42 }, CommaSeparatedIds.Parse("42"));
    }

    [Fact]
    public void ExtraWhitespaceAndEmptyEntries_AreTrimmedAndRemoved()
    {
        Assert.Equal(new List<int> { 1, 2, 3 }, CommaSeparatedIds.Parse(" 1 , , 2 ,3,"));
    }

    [Fact]
    public void MixOfValidAndGarbageTokens_SkipsGarbageInsteadOfThrowing()
    {
        // This is the M1 fix: a single poisoned token (e.g. written by an external service like
        // studylife-ai) must not throw and take down the whole GET.
        Assert.Equal(new List<int> { 1, 3 }, CommaSeparatedIds.Parse("1,notanumber,3"));
    }

    [Fact]
    public void AllTokensGarbage_ReturnsEmptyListInsteadOfThrowing()
    {
        Assert.Empty(CommaSeparatedIds.Parse("abc,def,xyz"));
    }

    [Fact]
    public void WhitespaceOnly_ReturnsEmptyList()
    {
        Assert.Empty(CommaSeparatedIds.Parse("   "));
    }

    [Fact]
    public void DuplicateValues_AreKeptAsIs_NoDeduplication()
    {
        Assert.Equal(new List<int> { 5, 5, 5 }, CommaSeparatedIds.Parse("5,5,5"));
    }

    [Fact]
    public void NegativeAndZeroValues_AreAcceptedAsIs()
    {
        Assert.Equal(new List<int> { -1, 0, 1 }, CommaSeparatedIds.Parse("-1,0,1"));
    }
}
