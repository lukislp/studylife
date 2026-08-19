using Markdig;

namespace StudyLife.Tts;

/// <summary>
/// Notes support a togglable Markdown mode (see StudyLife.Client's Notes.razor) - this
/// strips Markdown syntax before text reaches the phonemizer, so "hashtag hashtag Chapter
/// one" doesn't get read out literally. Plain-text notes pass through unchanged.
/// </summary>
public static class MarkdownToSpeechText
{
    // Same pipeline configuration as Notes.razor's live preview (UseAdvancedExtensions +
    // DisableHtml) - one shared instance, Markdig recommends reuse (immutable/thread-safe
    // once built). Note: DisableHtml has no effect on ToPlainText specifically (verified) -
    // raw HTML in a note passes through as literal text either way. That's fine here: this
    // output only ever reaches the phonemizer, never a browser, so there is no HTML-rendering
    // surface to protect against - kept for consistency with the live-preview pipeline, not
    // because it does anything for this code path.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string Extract(string? content, bool isMarkdown) =>
        isMarkdown ? Markdown.ToPlainText(content ?? "", Pipeline) : content ?? "";
}
