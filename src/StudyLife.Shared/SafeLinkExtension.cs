// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace StudyLife.Shared;

/// <summary>
/// Markdig extension that enforces <see cref="MarkdownUrlPolicy"/> on the parsed document,
/// before any renderer sees it: a link with a refused target becomes its plain text, an image
/// becomes its alt text and an autolink (<c>&lt;scheme:...&gt;</c>) becomes the literal URL.
/// Working on the syntax tree covers every renderer at once (HTML, plain text, media links)
/// and every way a URL gets in (inline, reference definition, autolink, entity-encoded).
/// </summary>
internal sealed class SafeLinkExtension : IMarkdownExtension
{
    // Subscribed after the other extensions' handlers (this extension is added last), so links
    // the AutoIdentifiers extension resolves are already in place.
    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += EnforceUrlPolicy;

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void EnforceUrlPolicy(MarkdownDocument document)
    {
        // Materialised first: the loop changes the tree it walks.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!IsAllowed(link))
            {
                Unwrap(link);
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (!MarkdownUrlPolicy.IsAllowed(autolink.Url))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }
    }

    private static bool IsAllowed(LinkInline link) =>
        MarkdownUrlPolicy.IsAllowed(link.Url)
        && (link.GetDynamicUrl is null || MarkdownUrlPolicy.IsAllowed(link.GetDynamicUrl()));

    // Moves the link's children (its text, or an image's alt text) in front of it, then drops
    // the link itself.
    private static void Unwrap(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }
        link.Remove();
    }
}
