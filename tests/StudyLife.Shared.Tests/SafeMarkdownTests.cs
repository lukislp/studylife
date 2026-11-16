// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Markdig;
using Markdig.Extensions.GenericAttributes;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Regression tests for stored XSS through Markdown notes (audit 2026-11-16, finding H1): notes
/// can be written by the AI, MCP and capture keys, so their Markdown is untrusted.
/// </summary>
public class SafeMarkdownTests
{
    // ---- The payloads from the audit report ----

    [Fact]
    public void ImageWithOnerrorAttribute_RendersNoEventHandler()
    {
        var html = SafeMarkdown.ToHtml("![img](x){onerror=alert(1)}");

        Assert.DoesNotContain("onerror=\"", html);
        Assert.Contains("<img src=\"x\"", html);
    }

    [Fact]
    public void JavascriptLink_RendersOnlyTheLinkText()
    {
        var html = SafeMarkdown.ToHtml("[click](javascript:alert(1))");

        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<p>click</p>\n", html);
    }

    [Fact]
    public void ParagraphWithStyleAttribute_RendersNoStyle()
    {
        var html = SafeMarkdown.ToHtml("para {style=\"position:fixed;inset:0\"}");

        Assert.DoesNotContain("style=\"", html);
        Assert.StartsWith("<p>", html);
    }

    // ---- Generic attributes on other elements ----

    [Theory]
    [InlineData("# Title {onclick=alert(1)}")]
    [InlineData("# Title {#id .cls onmouseover=alert(1)}")]
    [InlineData("[link](https://example.com){onclick=alert(1)}")]
    [InlineData("[link](https://example.com){style=\"position:fixed;inset:0\"}")]
    [InlineData("*em*{onmouseover=alert(1)}")]
    [InlineData("{onclick=alert(1)}\nparagraph")]
    [InlineData("```js {onclick=alert(1)}\ncode\n```")]
    [InlineData("- item {style=\"display:none\"}")]
    public void GenericAttributes_AreNeverRendered(string markdown)
    {
        var html = SafeMarkdown.ToHtml(markdown);

        Assert.DoesNotContain("onclick=\"", html);
        Assert.DoesNotContain("onmouseover=\"", html);
        Assert.DoesNotContain("style=\"", html);
    }

    // ---- Refused URL schemes, including obfuscated spellings ----

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JAVASCRIPT:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](  javascript:alert(1))")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](&#x6A;avascript:alert(1))")]
    [InlineData("[x](javascript&colon;alert(1))")]
    [InlineData("[x](java&#09;script:alert(1))")]
    [InlineData("[x](java&#10;script:alert(1))")]
    [InlineData("[x](&#1;javascript:alert(1))")]
    [InlineData("[x](<javascript:alert(1)>)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](VBScript:msgbox(1))")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("[x][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("[x][ref]\n\n[ref]: <&#106;avascript:alert(1)>")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<JAVASCRIPT:alert(1)>")]
    [InlineData("<vbscript:msgbox(1)>")]
    [InlineData("<data:text/html,alert(1)>")]
    public void DangerousLinkTargets_NeverBecomeAnHref(string markdown)
    {
        var html = SafeMarkdown.ToHtml(markdown);

        Assert.DoesNotContain("href=", html);
    }

    [Theory]
    [InlineData("![img](javascript:alert(1))")]
    [InlineData("![img](JAVASCRIPT:alert(1))")]
    [InlineData("![img](data:image/svg+xml,<svg onload=alert(1)>)")]
    [InlineData("![img](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("![img](vbscript:msgbox(1))")]
    [InlineData("![img][ref]\n\n[ref]: data:text/html,x")]
    public void DangerousImageSources_NeverBecomeAnImage(string markdown)
    {
        var html = SafeMarkdown.ToHtml(markdown);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
        Assert.Contains("img", html); // the alt text stays
    }

    [Fact]
    public void LinkAroundDangerousImage_RemovesBoth_KeepsTheAltText()
    {
        var html = SafeMarkdown.ToHtml("[![alt](javascript:a)](javascript:b)");

        Assert.Equal("<p>alt</p>\n", html);
    }

    [Fact]
    public void DangerousLink_KeepsItsFormattedText()
    {
        var html = SafeMarkdown.ToHtml("[**bold** text](javascript:alert(1)) after");

        Assert.Equal("<p><strong>bold</strong> text after</p>\n", html);
    }

    [Fact]
    public void DangerousAutolink_StaysVisibleAsText()
    {
        var html = SafeMarkdown.ToHtml("<javascript:alert(1)>");

        Assert.Equal("<p>javascript:alert(1)</p>\n", html);
    }

    // ---- Allowed targets keep working ----

    [Theory]
    [InlineData("[x](https://example.com/a?b=c#d)", "href=\"https://example.com/a?b=c#d\"")]
    [InlineData("[x](HTTP://example.com)", "href=\"HTTP://example.com\"")]
    [InlineData("[x](mailto:someone@example.com)", "href=\"mailto:someone@example.com\"")]
    [InlineData("[x](/notes/12)", "href=\"/notes/12\"")]
    [InlineData("[x](notes/a:b)", "href=\"notes/a:b\"")]
    [InlineData("[x](#section)", "href=\"#section\"")]
    [InlineData("[x](?q=a:b)", "href=\"?q=a:b\"")]
    [InlineData("[x](//example.com/path)", "href=\"//example.com/path\"")]
    [InlineData("[x][ref]\n\n[ref]: https://example.com", "href=\"https://example.com\"")]
    [InlineData("<https://example.com>", "href=\"https://example.com\"")]
    [InlineData("<someone@example.com>", "href=\"mailto:someone@example.com\"")]
    [InlineData("see www.example.com today", "href=\"http://www.example.com\"")]
    [InlineData("see https://example.com today", "href=\"https://example.com\"")]
    [InlineData("![pic](https://example.com/a.png)", "src=\"https://example.com/a.png\"")]
    [InlineData("![pic](images/a.png)", "src=\"images/a.png\"")]
    public void SafeTargets_AreRenderedUnchanged(string markdown, string expected)
    {
        Assert.Contains(expected, SafeMarkdown.ToHtml(markdown));
    }

    [Fact]
    public void LinkToAHeading_ResolvedByAutoIdentifiers_StillWorks()
    {
        var html = SafeMarkdown.ToHtml("# My Heading\n\nSee [My Heading].");

        Assert.Contains("href=\"#my-heading\"", html);
    }

    [Fact]
    public void RawHtml_IsEscaped()
    {
        var html = SafeMarkdown.ToHtml("<script>alert(1)</script>\n\ntext <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    // ---- The features notes rely on stay available ----

    [Theory]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<table>")]
    [InlineData("+---+---+\n| a | b |\n+===+===+\n| 1 | 2 |\n+---+---+", "<table>")]
    [InlineData("- [x] done\n- [ ] open", "type=\"checkbox\"")]
    [InlineData("~~gone~~", "<del>gone</del>")]
    [InlineData("H~2~O", "<sub>2</sub>")]
    [InlineData("x^2^", "<sup>2</sup>")]
    [InlineData("++inserted++", "<ins>inserted</ins>")]
    [InlineData("==marked==", "<mark>marked</mark>")]
    [InlineData("Text[^1]\n\n[^1]: The note.", "class=\"footnotes\"")]
    [InlineData("# Heading", "<h1 id=\"heading\">")]
    [InlineData("Term\n:   Definition", "<dl>")]
    [InlineData("a. one\nb. two", "<ol type=\"a\">")]
    [InlineData("$x^2$", "class=\"math\"")]
    [InlineData("```mermaid\ngraph TD;\n```", "class=\"mermaid\"")]
    [InlineData("> [!NOTE]\n> Hint", "markdown-alert-note")]
    [InlineData("::: tip\ncontent\n:::", "<div class=\"tip\">")]
    [InlineData("^^^\n![pic](a.png)\n^^^ Caption", "<figure")]
    [InlineData("*[HTML]: Hyper Text\n\nHTML here", "<abbr title=\"Hyper Text\">")]
    [InlineData("\"\"Some Book\"\"", "<cite>")]
    [InlineData("^^ footer text", "<footer>")]
    [InlineData("![video](https://www.youtube.com/watch?v=abc)", "src=\"https://www.youtube.com/embed/abc\"")]
    public void AdvancedExtensionFeatures_StillRender(string markdown, string expected)
    {
        Assert.Contains(expected, SafeMarkdown.ToHtml(markdown));
    }

    [Fact]
    public void Pipeline_HasEveryAdvancedExtensionExceptGenericAttributes()
    {
        // Guards the spelled-out extension list against drift: a Markdig update that adds an
        // extension to UseAdvancedExtensions shows up here as a deliberate decision.
        var advanced = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()
            .Extensions.Select(e => e.GetType())
            .Where(t => t != typeof(GenericAttributesExtension))
            .ToHashSet();
        var safe = SafeMarkdown.Pipeline.Extensions.Select(e => e.GetType())
            .Where(t => t != typeof(SafeLinkExtension))
            .ToHashSet();

        Assert.Equal(advanced.OrderBy(t => t.FullName), safe.OrderBy(t => t.FullName));
        Assert.DoesNotContain(typeof(GenericAttributesExtension), safe);
        Assert.Contains(SafeMarkdown.Pipeline.Extensions, e => e is SafeLinkExtension);
    }

    // ---- Plain text (text to speech) ----

    [Fact]
    public void ToPlainText_DropsSyntax_KeepsText()
    {
        var text = SafeMarkdown.ToPlainText("# Title\n\nSome **bold** [link](https://example.com).");

        Assert.Contains("Title", text);
        Assert.Contains("Some bold link.", text);
        Assert.DoesNotContain("https://example.com", text);
    }

    [Fact]
    public void ToPlainText_OfDangerousLink_KeepsTheLinkText()
    {
        Assert.Contains("click", SafeMarkdown.ToPlainText("[click](javascript:alert(1))"));
    }

    [Fact]
    public void NullContent_RendersEmpty()
    {
        Assert.Equal("", SafeMarkdown.ToHtml(null));
        Assert.Equal("", SafeMarkdown.ToPlainText(null));
    }
}
