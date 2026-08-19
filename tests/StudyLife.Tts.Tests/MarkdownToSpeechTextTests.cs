using StudyLife.Tts;

namespace StudyLife.Tts.Tests;

public class MarkdownToSpeechTextTests
{
    [Fact]
    public void PlainTextNote_PassesThroughUnchanged_EvenWithMarkdownLikeCharacters()
    {
        var content = "Check the # of items and *emphasize* this.";
        Assert.Equal(content, MarkdownToSpeechText.Extract(content, isMarkdown: false));
    }

    [Fact]
    public void Heading_LosesHashMarkers()
    {
        var result = MarkdownToSpeechText.Extract("# Chapter One", isMarkdown: true);
        Assert.DoesNotContain("#", result);
        Assert.Contains("Chapter One", result);
    }

    [Fact]
    public void BoldAndItalic_LoseTheirMarkers()
    {
        var result = MarkdownToSpeechText.Extract("This is **bold** and *italic* text.", isMarkdown: true);
        Assert.DoesNotContain("*", result);
        Assert.Contains("This is bold and italic text.", result);
    }

    [Fact]
    public void ListItems_LoseBulletMarkers()
    {
        var result = MarkdownToSpeechText.Extract("- item one\n- item two", isMarkdown: true);
        Assert.DoesNotContain("-", result);
        Assert.Contains("item one", result);
        Assert.Contains("item two", result);
    }

    [Fact]
    public void Link_KeepsVisibleTextOnly_DropsUrl()
    {
        var result = MarkdownToSpeechText.Extract("Check out [this link](https://example.com) for more.", isMarkdown: true);
        Assert.Contains("this link", result);
        Assert.DoesNotContain("https://example.com", result);
    }

    [Fact]
    public void Blockquote_LosesQuoteMarker()
    {
        var result = MarkdownToSpeechText.Extract("> A quote worth remembering", isMarkdown: true);
        Assert.DoesNotContain(">", result);
        Assert.Contains("A quote worth remembering", result);
    }

    [Fact]
    public void InlineCode_LosesBackticks()
    {
        var result = MarkdownToSpeechText.Extract("Some `inline code` here.", isMarkdown: true);
        Assert.DoesNotContain("`", result);
        Assert.Contains("inline code", result);
    }

    [Fact]
    public void NullContent_ReturnsEmptyString_ForBothModes()
    {
        Assert.Equal("", MarkdownToSpeechText.Extract(null, isMarkdown: false));
        Assert.Equal("", MarkdownToSpeechText.Extract(null, isMarkdown: true));
    }
}
