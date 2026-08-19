using StudyLife.Tts;

namespace StudyLife.Tts.Tests;

public class TextChunkerTests
{
    [Fact]
    public void ShortText_ReturnsSingleChunk()
    {
        var chunks = TextChunker.Chunk("Ein kurzer Satz.").ToList();
        Assert.Single(chunks);
        Assert.Equal("Ein kurzer Satz.", chunks[0]);
    }

    [Fact]
    public void EveryChunk_NeverExceedsMaxLength()
    {
        var longText = string.Join(" ", Enumerable.Repeat("Dies ist ein Testsatz mit ein paar Wörtern.", 50));
        var chunks = TextChunker.Chunk(longText, maxChunkLength: 100).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 100, $"chunk exceeded cap: \"{c}\" ({c.Length} chars)"));
    }

    [Fact]
    public void UnpunctuatedLineLongerThanCap_IsHardWrapped()
    {
        var longWord = string.Join(" ", Enumerable.Repeat("Wort", 60)); // no sentence punctuation at all
        var chunks = TextChunker.Chunk(longWord, maxChunkLength: 50).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 50));
    }

    [Fact]
    public void MultipleLines_EachHandledIndependently()
    {
        var text = "Zeile eins.\nZeile zwei.\nZeile drei.";
        var chunks = TextChunker.Chunk(text, maxChunkLength: 300).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Zeile eins.", chunks[0]);
        Assert.Equal("Zeile zwei.", chunks[1]);
        Assert.Equal("Zeile drei.", chunks[2]);
    }

    [Fact]
    public void EmptyLines_AreSkipped()
    {
        var chunks = TextChunker.Chunk("Erste Zeile.\n\n\nZweite Zeile.").ToList();
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void EmptyInput_ReturnsNoChunks()
    {
        Assert.Empty(TextChunker.Chunk(""));
        Assert.Empty(TextChunker.Chunk("   \n  \n "));
    }

    [Fact]
    public void ConcatenatingChunks_PreservesAllWords()
    {
        var text = "Erster Satz hier. Zweiter Satz da. Dritter Satz überall, mit mehr Wörtern als die anderen beiden zusammen.";
        var chunks = TextChunker.Chunk(text, maxChunkLength: 30).ToList();
        var rejoined = string.Join(" ", chunks);

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word, rejoined);
    }
}
