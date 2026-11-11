// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// The motivation styles (Setup &gt; Motivational Style) and the one place that knows the retired
/// storage value of the first of them. The style key is persisted in <c>UserSettings</c>, travels
/// in the settings DTO (API clients, native apps compiled against an older client) and in backup
/// files, so a key that is renamed must still be read from all of those.
/// <para>
/// Writers store <see cref="Canonical"/>; every reader that takes a value from storage, the wire or
/// an import passes it through <see cref="Canonical"/> too. That keeps the rename migration-free:
/// old database rows are upgraded on read and rewritten on the next settings save.
/// </para>
/// </summary>
public static class MotivationalStyles
{
    public const string Thoughtful = "thoughtful";
    public const string Zen = "zen";
    public const string Intense = "intense";
    public const string Hype = "hype";

    /// <summary>Stored value of <see cref="Thoughtful"/> before it got a neutral name. Accepted
    /// everywhere a style is read, never written. Do not use it anywhere else.</summary>
    public const string LegacyThoughtful = "claude";

    /// <summary>The style new settings start with.</summary>
    public const string Default = Thoughtful;

    /// <summary>Every style that has a quote pool in the server text catalogue.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Thoughtful, Zen, Intense, Hype };

    /// <summary>Maps a retired style key to its current one; every other value (including
    /// unknown ones, which the quote picker handles) passes through unchanged.</summary>
    public static string Canonical(string? style) =>
        string.Equals(style, LegacyThoughtful, StringComparison.Ordinal) ? Thoughtful : style ?? "";

    /// <summary><see cref="Canonical"/>, then <see cref="Default"/> for anything that is not a
    /// known style.</summary>
    public static string ResolveOrDefault(string? style)
    {
        var canonical = Canonical(style);
        return All.Contains(canonical) ? canonical : Default;
    }
}
