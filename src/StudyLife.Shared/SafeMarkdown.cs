// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Markdig;

namespace StudyLife.Shared;

/// <summary>
/// The one Markdown pipeline of StudyAtrium: every place that turns a note's Markdown into HTML
/// or plain text goes through here (client preview and print view, client and server text to
/// speech), so the safety rules live in exactly one place.
/// <para>Notes are untrusted input - the AI, MCP and capture keys can create them and backups
/// carry them - so the pipeline:</para>
/// <list type="bullet">
/// <item>escapes raw HTML (<c>DisableHtml</c>);</item>
/// <item>has no generic attributes (<c>{onerror=...}</c>, <c>{style=...}</c>): the extension
/// list is the one of <c>UseAdvancedExtensions()</c> minus <c>UseGenericAttributes()</c>,
/// spelled out so a Markdig update cannot add an extension silently;</item>
/// <item>drops link and image targets that <see cref="MarkdownUrlPolicy"/> refuses
/// (<see cref="SafeLinkExtension"/>).</item>
/// </list>
/// </summary>
public static class SafeMarkdown
{
    // One shared instance: Markdig pipelines are immutable and thread-safe once built.
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAlertBlocks()
        .UseAbbreviations()
        .UseAutoIdentifiers()
        .UseCitations()
        .UseCustomContainers()
        .UseDefinitionLists()
        .UseEmphasisExtras()
        .UseFigures()
        .UseFooters()
        .UseFootnotes()
        .UseGridTables()
        .UseMathematics()
        .UseMediaLinks()
        .UsePipeTables()
        .UseListExtras()
        .UseTaskLists()
        .UseDiagrams()
        .UseAutoLinks()
        .DisableHtml()
        .Use<SafeLinkExtension>()
        .Build();

    /// <summary>Renders a note's Markdown to HTML that is safe to insert into the page.</summary>
    public static string ToHtml(string? markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);

    /// <summary>The note's text without Markdown syntax (for text to speech).</summary>
    public static string ToPlainText(string? markdown) => Markdown.ToPlainText(markdown ?? "", Pipeline);
}
