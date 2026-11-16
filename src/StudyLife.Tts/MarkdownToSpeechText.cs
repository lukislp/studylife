// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Tts;

/// <summary>
/// Notes support a togglable Markdown mode (see StudyLife.Client's Notes.razor) - this
/// strips Markdown syntax before text reaches the phonemizer, so "hashtag hashtag Chapter
/// one" doesn't get read out literally. Plain-text notes pass through unchanged.
/// </summary>
public static class MarkdownToSpeechText
{
    // The shared pipeline of the client's Notes page, so speech reads the same text the
    // preview shows. This output only reaches the phonemizer, never a browser.
    public static string Extract(string? content, bool isMarkdown) =>
        isMarkdown ? SafeMarkdown.ToPlainText(content) : content ?? "";
}
