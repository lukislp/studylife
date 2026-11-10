// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;

namespace StudyLife.Client.Services;

/// <summary>How exactly a <see cref="SourceLink"/> points at the code of a running build.</summary>
public enum SourceLinkKind
{
    /// <summary>The commit the build was made from (the "+sha" part of the version).</summary>
    Commit,
    /// <summary>The release tag of the version (v1.2.3).</summary>
    Tag,
    /// <summary>The repository itself: a development build that maps to no tag or commit.</summary>
    Repository,
}

public sealed record SourceLink(string Url, SourceLinkKind Kind)
{
    /// <summary>True when the link leads to exactly the code of the build, not just to the repository.</summary>
    public bool IsExact => Kind != SourceLinkKind.Repository;
}

/// <summary>Which part of the running software a row of the About section describes.</summary>
public enum AboutBuildKind
{
    Server,
    /// <summary>The user interface running in the browser, shown only when it differs from the server
    /// (a cached bundle that the service worker has not replaced yet).</summary>
    Browser,
    /// <summary>The native app shell (the apps compile the client into their bundle, so their own
    /// version is the one that identifies the interface the user is looking at).</summary>
    App,
}

/// <summary>One build the user is running: its display version (null when unknown) and the source
/// of exactly that build.</summary>
public sealed record AboutBuild(AboutBuildKind Kind, string? Version, SourceLink Source);

/// <summary>
/// The "source code of exactly this version" rule of the About section (AGPL-3.0 section 13: users
/// of a network service must be able to get the source of the version they interact with). Pure
/// functions on version strings, so the rule is unit-tested without a browser or a server.
///
/// Where the version comes from: CI builds with <c>-p:Version=&lt;release&gt;</c> and the .NET SDK adds
/// the commit of the checkout, so the assembly's informational version reads
/// <c>3.26.5+0123abc...</c>. The commit identifies the build exactly; the release tag
/// (<c>v3.26.5</c>, the tag the release pipeline creates, also used by the Codespaces link of
/// <see cref="ProblemReportLink"/>) is the fallback when there is no commit; a build with neither
/// links the repository and says so.
/// </summary>
public static class AboutSource
{
    public const string ServerRepository = "https://github.com/lukislp/studylife";
    public const string AppRepository = "https://github.com/lukislp/studylife-app";

    private static readonly Regex CommitHash =
        new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The version without build metadata ("3.26.5+abc" becomes "3.26.5"); null for empty input.</summary>
    public static string? CoreVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var core = informationalVersion.Split('+')[0].Trim();
        return core.Length == 0 ? null : core;
    }

    /// <summary>The commit hash after the "+" of an informational version, or null when there is
    /// none or it is not a plain hexadecimal commit hash.</summary>
    public static string? CommitOf(string? informationalVersion)
    {
        if (informationalVersion is null) return null;
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0) return null;
        // Build metadata may carry more dot-separated identifiers after the hash.
        var candidate = informationalVersion[(plus + 1)..].Split('.')[0].Trim();
        return CommitHash.IsMatch(candidate) ? candidate.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The link to the source of a build of <paramref name="repository"/>. Only validated pieces (a
    /// hexadecimal commit hash or a release version) ever end up in the URL, never the raw string.
    /// </summary>
    public static SourceLink Resolve(string repository, string? informationalVersion)
    {
        if (CommitOf(informationalVersion) is { } commit)
            return new SourceLink($"{repository}/tree/{commit}", SourceLinkKind.Commit);
        if (CoreVersion(informationalVersion) is { } core && ProblemReportLink.IsReleaseVersion(core))
            return new SourceLink($"{repository}/tree/v{core}", SourceLinkKind.Tag);
        return new SourceLink(repository, SourceLinkKind.Repository);
    }

    /// <summary>
    /// The rows of the About section. The server is always listed. A native app adds its own
    /// version (its source is the app repository); the browser adds its interface version only when
    /// it differs from the server's, which is the case worth seeing (a cached older bundle).
    /// </summary>
    /// <param name="serverVersion">Informational version from GET api/system/version, or null when unreachable.</param>
    /// <param name="browserVersion">Informational version of the client assembly in the browser.</param>
    /// <param name="appVersion">Display version of the native app shell, null in the browser.</param>
    /// <param name="isNativeApp">True inside a native app shell.</param>
    public static IReadOnlyList<AboutBuild> Compose(
        string? serverVersion, string? browserVersion, string? appVersion, bool isNativeApp)
    {
        var rows = new List<AboutBuild>
        {
            new(AboutBuildKind.Server, CoreVersion(serverVersion), Resolve(ServerRepository, serverVersion)),
        };

        if (isNativeApp)
        {
            rows.Add(new(AboutBuildKind.App, CoreVersion(appVersion), Resolve(AppRepository, appVersion)));
        }
        else if (CoreVersion(browserVersion) is { } browserCore && browserCore != CoreVersion(serverVersion))
        {
            rows.Add(new(AboutBuildKind.Browser, browserCore, Resolve(ServerRepository, browserVersion)));
        }

        return rows;
    }
}
