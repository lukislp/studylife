using StudyLife.Tts;

namespace StudyLife.Tts.Tests;

public class TextChunkerTests
{
    [Fact]
    public void ShortText_ReturnsSingleChunk()
    {
        var chunks = TextChunker.Chunk("Ein kurzer Satz.").ToList();
        Assert.Single(chunks);
        Assert.Equal("Ein kurzer Satz.", chunks[0].Text);
        Assert.True(chunks[0].LongPause);
    }

    [Fact]
    public void EveryChunk_NeverExceedsMaxLength()
    {
        var longText = string.Join(" ", Enumerable.Repeat("Dies ist ein Testsatz mit ein paar Wörtern.", 50));
        var chunks = TextChunker.Chunk(longText, maxChunkLength: 100).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100, $"chunk exceeded cap: \"{c.Text}\" ({c.Text.Length} chars)"));
    }

    [Fact]
    public void UnpunctuatedLineLongerThanCap_IsHardWrapped()
    {
        var longWord = string.Join(" ", Enumerable.Repeat("Wort", 60)); // no sentence punctuation at all
        var chunks = TextChunker.Chunk(longWord, maxChunkLength: 50).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 50));
    }

    [Fact]
    public void MultipleLines_EachHandledIndependently()
    {
        var text = "Zeile eins.\nZeile zwei.\nZeile drei.";
        var chunks = TextChunker.Chunk(text, maxChunkLength: 300).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Zeile eins.", chunks[0].Text);
        Assert.Equal("Zeile zwei.", chunks[1].Text);
        Assert.Equal("Zeile drei.", chunks[2].Text);
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
        var rejoined = string.Join(" ", chunks.Select(c => c.Text));

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word, rejoined);
    }

    [Theory]
    [InlineData("Erster Teil, zweiter Teil.")]
    [InlineData("Erster Teil; zweiter Teil.")]
    [InlineData("Erster Teil: zweiter Teil.")]
    public void ClauseLevelPunctuation_SplitsIntoShortPauseChunks(string text)
    {
        var chunks = TextChunker.Chunk(text).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.False(chunks[0].LongPause, $"first chunk of \"{text}\" should be a short pause");
        Assert.True(chunks[1].LongPause, "sentence-ending chunk should be a long pause");
    }

    [Theory]
    [InlineData("Ein Satz. Noch einer.")]
    [InlineData("Ein Satz! Noch einer.")]
    [InlineData("Ein Satz? Noch einer.")]
    [InlineData("Ein Satz… Noch einer.")]
    public void SentenceEnders_SplitIntoLongPauseChunks(string text)
    {
        var chunks = TextChunker.Chunk(text).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[0].LongPause, $"first chunk of \"{text}\" should be a long pause");
    }

    [Fact]
    public void EmDashAndClosingParen_AreShortPauses()
    {
        var chunks = TextChunker.Chunk("Ein Einschub — wie dieser hier — geht weiter.").ToList();
        Assert.True(chunks.Count >= 2);
        Assert.False(chunks[0].LongPause);
    }

    [Fact]
    public void PlainHyphenInCompoundWord_DoesNotSplit()
    {
        // A literal ASCII hyphen must NOT be treated as a pause - it's overwhelmingly used
        // inside compound words in German note content (Wort-Vektorisierung, Skip-Gram, ...).
        var chunks = TextChunker.Chunk("Wort-Vektorisierung ist ein Kernthema.").ToList();
        Assert.Single(chunks);
        Assert.Equal("Wort-Vektorisierung ist ein Kernthema.", chunks[0].Text);
    }
}
