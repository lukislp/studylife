// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.I18nText;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>The "source code of exactly this version" rule of the About section.</summary>
public class AboutSourceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Build_with_a_commit_links_that_commit()
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, $"3.26.5+{Hash}");

        Assert.Equal($"https://github.com/lukislp/studylife/tree/{Hash}", link.Url);
        Assert.Equal(SourceLinkKind.Commit, link.Kind);
        Assert.True(link.IsExact);
    }

    [Fact]
    public void Commit_wins_over_the_tag_and_is_lower_cased()
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, "3.26.5+ABCDEF1");

        Assert.Equal("https://github.com/lukislp/studylife/tree/abcdef1", link.Url);
    }

    [Fact]
    public void Extra_build_metadata_after_the_hash_is_ignored()
    {
        Assert.Equal("abcdef1", AboutSource.CommitOf("1.2.3+abcdef1.dirty"));
    }

    [Theory]
    [InlineData("3.26.5", "https://github.com/lukislp/studylife/tree/v3.26.5")]
    [InlineData("3.27.0-rc.1", "https://github.com/lukislp/studylife/tree/v3.27.0-rc.1")]
    [InlineData("3.26.5+", "https://github.com/lukislp/studylife/tree/v3.26.5")]
    public void Release_without_a_commit_links_the_tag(string version, string url)
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, version);

        Assert.Equal(url, link.Url);
        Assert.Equal(SourceLinkKind.Tag, link.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("1.0.0")]
    [InlineData("0.0.0-alpha")]
    [InlineData("1.0")]
    public void Development_build_links_the_repository_and_says_it_is_not_exact(string? version)
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, version);

        Assert.Equal(AboutSource.ServerRepository, link.Url);
        Assert.Equal(SourceLinkKind.Repository, link.Kind);
        Assert.False(link.IsExact);
    }

    [Theory]
    [InlineData("3.26.5+abc/../../evil")]
    [InlineData("3.26.5+zzzzzzz")]
    [InlineData("3.26.5+abc")] // too short for a commit hash
    public void A_commit_that_is_not_a_hash_never_reaches_the_url(string version)
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, version);

        Assert.DoesNotContain("evil", link.Url);
        Assert.DoesNotContain("zzz", link.Url);
        Assert.Equal("https://github.com/lukislp/studylife/tree/v3.26.5", link.Url);
    }

    [Fact]
    public void A_version_that_is_not_a_version_never_reaches_the_url()
    {
        var link = AboutSource.Resolve(AboutSource.ServerRepository, "\"><script>alert(1)</script>");

        Assert.Equal(AboutSource.ServerRepository, link.Url);
    }

    [Fact]
    public void App_build_links_the_app_repository()
    {
        var link = AboutSource.Resolve(AboutSource.AppRepository, "1.9.5");

        Assert.Equal("https://github.com/lukislp/studylife-app/tree/v1.9.5", link.Url);
    }

    [Fact]
    public void Browser_with_the_servers_version_lists_only_the_server()
    {
        var rows = AboutSource.Compose($"3.26.5+{Hash}", $"3.26.5+{Hash}", null, isNativeApp: false);

        var row = Assert.Single(rows);
        Assert.Equal((AboutBuildKind.Server, "3.26.5"), (row.Kind, row.Version));
        Assert.Equal(SourceLinkKind.Commit, row.Source.Kind);
    }

    [Fact]
    public void Browser_running_a_cached_older_bundle_lists_both_with_their_own_source()
    {
        var rows = AboutSource.Compose("3.26.5+abcdef1", "3.25.0+1234567", null, isNativeApp: false);

        Assert.Equal([AboutBuildKind.Server, AboutBuildKind.Browser], rows.Select(r => r.Kind));
        Assert.Equal("3.25.0", rows[1].Version);
        Assert.EndsWith("/tree/1234567", rows[1].Source.Url);
        Assert.EndsWith("/tree/abcdef1", rows[0].Source.Url);
    }

    [Fact]
    public void Unreachable_server_still_lists_the_server_with_an_unknown_version()
    {
        var rows = AboutSource.Compose(null, "3.26.5+abcdef1", null, isNativeApp: false);

        Assert.Null(rows[0].Version);
        Assert.Equal(SourceLinkKind.Repository, rows[0].Source.Kind);
        Assert.Equal(AboutBuildKind.Browser, rows[1].Kind);
    }

    [Fact]
    public void Native_app_lists_server_and_app_and_ignores_the_unstamped_client_assembly()
    {
        var rows = AboutSource.Compose("3.26.5+abcdef1", "1.0.0", "1.9.5", isNativeApp: true);

        Assert.Equal([AboutBuildKind.Server, AboutBuildKind.App], rows.Select(r => r.Kind));
        Assert.Equal("1.9.5", rows[1].Version);
        Assert.Equal("https://github.com/lukislp/studylife-app/tree/v1.9.5", rows[1].Source.Url);
    }

    [Fact]
    public void Native_app_that_does_not_report_a_version_links_the_app_repository()
    {
        var rows = AboutSource.Compose("3.26.5", null, null, isNativeApp: true);

        Assert.Null(rows[1].Version);
        Assert.Equal(AboutSource.AppRepository, rows[1].Source.Url);
    }
}

/// <summary>Stands in for the browser (no overrides) or a native shell.</summary>
internal sealed class FakePlatform : IClientPlatform
{
    public string Name { get; init; } = "web";
    public string? AppVersion { get; init; }
    public Func<Task<string?>> Notices { get; init; } = () => Task.FromResult<string?>(null);
    public Task<string?> ReadBundledNoticesAsync() => Notices();
}

public class AboutStateTests
{
    private const string Notices = "# Third-party notices\n\nMIT\n";

    private static AboutState NewState(ScriptedServer server, IClientPlatform? platform = null, string? browser = "3.26.5") =>
        new(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") },
            platform ?? new FakePlatform(), browser);

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Builds_come_from_the_version_endpoint()
    {
        var server = new ScriptedServer().OnJson("GET api/system/version", new VersionResponseDto { Version = "3.26.5+abcdef1" });

        var rows = await NewState(server).LoadBuildsAsync();

        var row = Assert.Single(rows);
        Assert.Equal("3.26.5", row.Version);
        Assert.EndsWith("/tree/abcdef1", row.Source.Url);
    }

    [Fact]
    public async Task Builds_survive_a_server_that_does_not_answer()
    {
        var rows = await NewState(new ScriptedServer()).LoadBuildsAsync();

        Assert.Null(rows[0].Version);
    }

    [Fact]
    public async Task Web_notices_come_from_the_servers_static_file()
    {
        var server = new ScriptedServer().On("GET THIRD-PARTY-NOTICES.md", _ => Text(Notices));

        var notices = await NewState(server).LoadNoticesAsync();

        Assert.Equal(new AboutNotices(Notices, AboutNoticesOrigin.Server), notices);
    }

    [Fact]
    public async Task The_spa_fallback_page_is_not_mistaken_for_the_notices()
    {
        // An older server has no such file and answers every unknown path with index.html and 200.
        var server = new ScriptedServer().On("GET THIRD-PARTY-NOTICES.md", _ => Text("<!DOCTYPE html><html></html>"));

        Assert.Null(await NewState(server).LoadNoticesAsync());
    }

    [Fact]
    public async Task Missing_file_yields_no_notices()
    {
        Assert.Null(await NewState(new ScriptedServer()).LoadNoticesAsync());
    }

    [Fact]
    public async Task Native_app_prefers_the_notices_bundled_into_the_app()
    {
        var server = new ScriptedServer().On("GET THIRD-PARTY-NOTICES.md", _ => Text("# Third-party notices\n\nSERVER\n"));
        var platform = new FakePlatform { Name = "ios", Notices = () => Task.FromResult<string?>(Notices) };

        var notices = await NewState(server, platform).LoadNoticesAsync();

        Assert.Equal(new AboutNotices(Notices, AboutNoticesOrigin.App), notices);
        Assert.Equal(0, server.Count("GET THIRD-PARTY-NOTICES.md"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not the notices")]
    public async Task Native_app_without_usable_bundled_notices_falls_back_to_the_server(string? bundled)
    {
        var server = new ScriptedServer().On("GET THIRD-PARTY-NOTICES.md", _ => Text(Notices));
        var platform = new FakePlatform { Name = "ios", Notices = () => Task.FromResult(bundled) };

        var notices = await NewState(server, platform).LoadNoticesAsync();

        Assert.Equal(AboutNoticesOrigin.Server, notices!.Origin);
    }

    [Fact]
    public async Task Native_app_whose_bundle_cannot_be_read_falls_back_to_the_server()
    {
        var server = new ScriptedServer().On("GET THIRD-PARTY-NOTICES.md", _ => Text(Notices));
        var platform = new FakePlatform { Name = "ios", Notices = () => throw new FileNotFoundException() };

        var notices = await NewState(server, platform).LoadNoticesAsync();

        Assert.Equal(AboutNoticesOrigin.Server, notices!.Origin);
    }

    [Theory]
    [InlineData("# Third-party notices\n", true)]
    [InlineData("#BOM## Third-party notices\n", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("<html></html>", false)]
    [InlineData("  # Third-party notices", false)]
    public void Only_the_notices_file_passes_the_check(string? text, bool expected)
    {
        Assert.Equal(expected, AboutState.IsNoticesText(text?.Replace("#BOM#", ((char)0xFEFF).ToString())));
    }

    [Fact]
    public void An_absurdly_large_text_is_refused()
    {
        var huge = "# Third-party notices\n" + new string('x', AboutState.MaxNoticesLength);

        Assert.False(AboutState.IsNoticesText(huge));
    }

    [Fact]
    public void The_committed_notices_file_passes_its_own_check()
    {
        var path = Path.Combine(FindRepositoryRoot(), "THIRD-PARTY-NOTICES.md");

        Assert.True(AboutState.IsNoticesText(File.ReadAllText(path)));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

public class SetupAboutCardTests : BunitContext
{
    private static readonly AboutText Texts = new()
    {
        Heading = "About",
        Desc = "Versions and licence.",
        ServerLabel = "Server",
        BrowserLabel = "Interface in this browser",
        AppLabel = "App",
        Unknown = "unknown",
        SourceLink = "Source code",
        SourceExact = "Exactly this version",
        SourceRepo = "Repository only",
        NewTab = "opens in a new tab",
        LicenceLabel = "Licence",
        LicenceMeaning = "You may share it.",
        PrivacyPolicy = "Privacy policy",
        NoticesHeading = "Third-party notices",
        NoticesDesc = "Components.",
        ShowNotices = "Show notices",
        HideNotices = "Hide notices",
        Loading = "Loading",
        NoticesFailed = "Could not load.",
        FromApp = "From this app",
        FromServer = "From the server",
    };

    private const string NoticesBody = "# Third-party notices\n\n<script>alert(1)</script> & <b>bold</b>\n";

    private static ScriptedServer Server(string? notices = NoticesBody)
    {
        var server = new ScriptedServer().OnJson("GET api/system/version",
            new VersionResponseDto { Version = "3.26.5+0123456789abcdef0123456789abcdef01234567" });
        if (notices is not null)
            server.On("GET THIRD-PARTY-NOTICES.md", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(notices) });
        return server;
    }

    private IRenderedComponent<SetupAboutCard> RenderCard(ScriptedServer server, IClientPlatform? platform = null)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") };
        var state = new AboutState(http, platform ?? new FakePlatform(), "3.26.5");
        Services.AddSingleton(http);
        Services.AddSingleton(platform ?? new FakePlatform());
        return Render<SetupAboutCard>(p => p.Add(c => c.Text, Texts).Add(c => c.State, state));
    }

    [Fact]
    public void Shows_version_licence_and_the_source_of_exactly_this_build()
    {
        var cut = RenderCard(Server());

        cut.WaitForAssertion(() => Assert.Contains("v3.26.5", cut.Markup));
        var links = cut.FindAll("a").ToDictionary(a => a.TextContent.Trim(), a => a);
        var source = cut.FindAll("a").First(a => a.TextContent.Contains("Source code"));
        Assert.Equal("https://github.com/lukislp/studylife/tree/0123456789abcdef0123456789abcdef01234567", source.GetAttribute("href"));
        Assert.Equal("_blank", source.GetAttribute("target"));
        Assert.Contains("noopener", source.GetAttribute("rel"));
        Assert.Contains("AGPL-3.0-only", cut.Markup);
        Assert.Contains("You may share it.", cut.Markup);
        Assert.True(links.ContainsKey("AGPL-3.0-only"));
    }

    [Theory]
    [InlineData("de", "https://legal.lktec.org/studylife/")]
    [InlineData("de-AT", "https://legal.lktec.org/studylife/")]
    [InlineData("en", "https://legal.lktec.org/en/studylife/")]
    [InlineData("fr", "https://legal.lktec.org/en/studylife/")]
    [InlineData("ru", "https://legal.lktec.org/en/studylife/")]
    [InlineData(null, "https://legal.lktec.org/en/studylife/")]
    public void Privacy_policy_link_follows_the_language_and_opens_externally(string? language, string expected)
    {
        var http = new HttpClient(Server()) { BaseAddress = new Uri("https://studylife.test/") };
        var state = new AboutState(http, new FakePlatform(), "3.26.5");
        Services.AddSingleton(http);
        Services.AddSingleton<IClientPlatform>(new FakePlatform());
        var cut = Render<SetupAboutCard>(p => p.Add(c => c.Text, Texts).Add(c => c.State, state).Add(c => c.Language, language ?? "en"));

        var link = cut.FindAll("a").First(a => a.TextContent.Trim() == "Privacy policy");
        Assert.Equal(expected, link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pt-BR")]
    public void Privacy_policy_defaults_to_english(string? language) =>
        Assert.Equal(PrivacyPolicyLink.English, PrivacyPolicyLink.For(language));

    [Fact]
    public void Native_app_shows_its_own_version_and_source()
    {
        var platform = new FakePlatform { Name = "ios", AppVersion = "1.9.5" };
        var cut = RenderCard(Server(), platform);

        cut.WaitForAssertion(() => Assert.Contains("v1.9.5", cut.Markup));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "https://github.com/lukislp/studylife-app/tree/v1.9.5");
    }

    [Fact]
    public void Notices_are_collapsed_and_not_fetched_until_opened()
    {
        var server = Server();
        var cut = RenderCard(server);
        cut.WaitForAssertion(() => Assert.Contains("v3.26.5", cut.Markup));

        var button = cut.Find("button[aria-controls=about-notices]");
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("pre"));
        Assert.Equal(0, server.Count("GET THIRD-PARTY-NOTICES.md"));
    }

    [Fact]
    public void Opening_shows_the_notices_as_text_never_as_markup()
    {
        var cut = RenderCard(Server());
        cut.WaitForAssertion(() => Assert.Contains("v3.26.5", cut.Markup));

        cut.Find("button[aria-controls=about-notices]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("pre")));
        Assert.Equal("true", cut.Find("button[aria-controls=about-notices]").GetAttribute("aria-expanded"));
        Assert.Equal(NoticesBody, cut.Find("pre").TextContent);
        // The file's <script> and <b> were text, not elements.
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("pre b"));
        var region = cut.Find(".about-notices-text");
        Assert.Equal("0", region.GetAttribute("tabindex"));
        Assert.Equal("region", region.GetAttribute("role"));
        Assert.Equal("about-notices-title", region.GetAttribute("aria-labelledby"));
        Assert.Contains("From the server", cut.Markup);
    }

    [Fact]
    public void Closing_and_reopening_does_not_fetch_again()
    {
        var server = Server();
        var cut = RenderCard(server);
        cut.WaitForAssertion(() => Assert.Contains("v3.26.5", cut.Markup));

        cut.Find("button[aria-controls=about-notices]").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("pre")));
        cut.Find("button[aria-controls=about-notices]").Click();
        Assert.Equal("false", cut.Find("button[aria-controls=about-notices]").GetAttribute("aria-expanded"));
        cut.Find("button[aria-controls=about-notices]").Click();

        Assert.Equal(1, server.Count("GET THIRD-PARTY-NOTICES.md"));
    }

    [Fact]
    public void Missing_notices_are_announced_instead_of_shown_empty()
    {
        var cut = RenderCard(Server(notices: null));
        cut.WaitForAssertion(() => Assert.Contains("v3.26.5", cut.Markup));

        cut.Find("button[aria-controls=about-notices]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Could not load.", cut.Find("[role=alert]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("pre"));
    }

    [Fact]
    public void Native_app_shows_the_notices_of_its_own_bundle()
    {
        var platform = new FakePlatform { Name = "ios", AppVersion = "1.9.5", Notices = () => Task.FromResult<string?>(NoticesBody) };
        var cut = RenderCard(Server(notices: null), platform);
        cut.WaitForAssertion(() => Assert.Contains("v1.9.5", cut.Markup));

        cut.Find("button[aria-controls=about-notices]").Click();

        cut.WaitForAssertion(() => Assert.Contains("From this app", cut.Markup));
        Assert.Equal(NoticesBody, cut.Find("pre").TextContent);
    }
}
