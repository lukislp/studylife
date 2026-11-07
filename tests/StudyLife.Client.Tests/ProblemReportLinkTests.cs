// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class ProblemReportLinkTests
{
    private static readonly ProblemReportContext Release =
        new("3.26.5", "web", "Chrome 142 on Windows", "de", false);

    private static string Decoded(string url, string name)
    {
        var m = Regex.Match(url, "[?&]" + name + "=([^&]*)");
        Assert.True(m.Success, $"parameter {name} missing in {url}");
        return Uri.UnescapeDataString(m.Groups[1].Value);
    }

    [Fact]
    public void Release_build_prefills_version_and_environment()
    {
        var url = ProblemReportLink.Build(Release);

        Assert.StartsWith("https://github.com/lukislp/studylife/issues/new?template=bug_report.yml&", url);
        Assert.Equal("3.26.5", Decoded(url, "version"));
        Assert.Equal(
            "App: 3.26.5\nPlatform: web, Chrome 142 on Windows\nLocale: de\nDemo: no\n"
            + "Reproduce in Codespaces: https://codespaces.new/lukislp/studylife?ref=v3.26.5&quickstart=1",
            Decoded(url, "environment"));
    }

    [Fact]
    public void Values_are_url_encoded()
    {
        var url = ProblemReportLink.Build(Release);

        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("\n", url);
        // The & inside the Codespaces link must not leak out as a new query parameter.
        Assert.Contains("quickstart%3D1", url);
        Assert.DoesNotContain("&quickstart=1", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("1.0.0")]
    [InlineData("0.0.0-alpha")]
    [InlineData("3.26")]
    [InlineData("v3.26.5")]
    public void Dev_build_has_no_codespaces_line(string? version)
    {
        var url = ProblemReportLink.Build(Release with { Version = version });

        Assert.Null(ProblemReportLink.CodespacesUrl(version));
        Assert.DoesNotContain("Codespaces", Decoded(url, "environment"));
        Assert.StartsWith("App: ", Decoded(url, "environment"));
        Assert.True(Decoded(url, "version").Length > 0);
    }

    [Fact]
    public void Demo_flag_and_prerelease_versions_are_reported()
    {
        var url = ProblemReportLink.Build(Release with { Version = "3.27.0-rc.1", Demo = true });

        var env = Decoded(url, "environment");
        Assert.Contains("Demo: yes", env);
        Assert.Contains("ref=v3.27.0-rc.1&", env);
    }

    [Fact]
    public void Missing_browser_is_omitted()
    {
        var env = ProblemReportLink.BuildEnvironmentText(Release with { Platform = "app", Browser = "" });

        Assert.Contains("Platform: app\n", env);
    }

    [Fact]
    public void Oversized_values_keep_the_url_short()
    {
        var huge = new string('x', 50_000);
        var url = ProblemReportLink.Build(new ProblemReportContext(huge, huge, huge, huge, true));

        Assert.True(url.Length <= ProblemReportLink.MaxUrlLength, $"length {url.Length}");
    }

    [Fact]
    public void Control_characters_cannot_inject_extra_lines()
    {
        const string hostile = "x\r\nInjected: line";
        var env = ProblemReportLink.BuildEnvironmentText(
            new ProblemReportContext("3.26.5", hostile, hostile, hostile, false));

        Assert.DoesNotContain("\r", env);
        Assert.Equal(5, env.Split('\n').Length); // App, Platform, Locale, Demo, Codespaces
        Assert.DoesNotContain("\nInjected", env);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36", "Chrome 142 on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/141.0.0.0", "Edge 141 on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0", "Firefox 140 on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15", "Safari 18 on macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/142.0.0.0 Mobile/15E148 Safari/604.1", "Chrome 142 on iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Mobile Safari/537.36 OPR/90.0.0.0", "Opera 90 on Android")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36", "Chrome 142 on Linux")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148", "iOS")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void User_agent_is_reduced_to_browser_major_and_os(string? ua, string expected) =>
        Assert.Equal(expected, ProblemReportLink.DescribeUserAgent(ua));

    [Fact]
    public void Raw_user_agent_never_appears_in_the_environment()
    {
        const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/142.0.0.0 Safari/537.36";
        var env = ProblemReportLink.BuildEnvironmentText(
            Release with { Browser = ProblemReportLink.DescribeUserAgent(ua) });

        Assert.DoesNotContain("Mozilla", env);
        Assert.DoesNotContain("AppleWebKit", env);
    }

    [Fact]
    public void Form_declares_every_field_id_the_code_prefills()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "ISSUE_TEMPLATE", ProblemReportLink.TemplateFile));
        var ids = Regex.Matches(yaml, @"^\s+id:\s*(\S+)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Contains(ProblemReportLink.VersionField, ids);
        Assert.Contains(ProblemReportLink.EnvironmentField, ids);
        // Existing ids stay stable: reports and links in the wild refer to them.
        Assert.Contains("what", ids);
        Assert.Contains("logs", ids);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            $"Could not find StudyLife.sln above {AppContext.BaseDirectory}.");
    }
}
