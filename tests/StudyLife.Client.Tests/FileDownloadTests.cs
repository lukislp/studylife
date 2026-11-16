// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>Records what a component handed to <see cref="IFileDownloadService"/>; optionally fails
/// the way a device that cannot show its save/share sheet does.</summary>
internal sealed class RecordingFileDownloads : IFileDownloadService
{
    public List<FileDownload> Saved { get; } = [];
    public Exception? Failure { get; set; }

    public Task SaveAsync(FileDownload file, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        lock (Saved) Saved.Add(file);
        return Task.CompletedTask;
    }
}

public class FileDownloadNameTests
{
    [Theory]
    [InlineData("studyatrium-export-20261116.json", "studyatrium-export-20261116.json")]
    [InlineData("\"studyatrium-backup-20261116.db\"", "studyatrium-backup-20261116.db")]
    [InlineData("../../Library/Preferences/evil.plist", "evil.plist")]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("C:\\Users\\x\\codes.txt", "codes.txt")]
    [InlineData("a:b*c?d<e>f|g.txt", "a_b_c_d_e_f_g.txt")]
    [InlineData(".hidden", "hidden")]
    [InlineData("..", FileDownloadName.Fallback)]
    [InlineData("", FileDownloadName.Fallback)]
    [InlineData(null, FileDownloadName.Fallback)]
    [InlineData("dir/", FileDownloadName.Fallback)]
    public void Sanitize_LeavesOneHarmlessPathSegment(string? input, string expected) =>
        Assert.Equal(expected, FileDownloadName.Sanitize(input));

    [Fact]
    public void Sanitize_ReplacesControlCharacters_AndCapsTheLength()
    {
        Assert.Equal("a_b.txt", FileDownloadName.Sanitize("a\nb.txt"));
        Assert.Equal(200, FileDownloadName.Sanitize(new string('x', 500)).Length);
    }

    [Fact]
    public void AFileDownload_AlwaysCarriesTheSanitizedName() =>
        Assert.Equal("evil.txt", new FileDownload("../evil.txt", [1]).FileName);
}

public class FileDownloadServiceExtensionsTests
{
    [Fact]
    public async Task SaveResponseAsync_TakesTheNameFromContentDisposition()
    {
        var downloads = new RecordingFileDownloads();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"server-name.db\"" };

        await downloads.SaveResponseAsync(response, "fallback.db");

        var file = Assert.Single(downloads.Saved);
        Assert.Equal("server-name.db", file.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Content);
    }

    [Fact]
    public async Task SaveResponseAsync_UsesTheFallbackName_WithoutContentDisposition()
    {
        var downloads = new RecordingFileDownloads();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        await downloads.SaveResponseAsync(response, "studyatrium-export-20261116.json");

        Assert.Equal("studyatrium-export-20261116.json", Assert.Single(downloads.Saved).FileName);
    }

    [Fact]
    public async Task SaveTextAsync_WritesUtf8WithoutByteOrderMark()
    {
        var downloads = new RecordingFileDownloads();

        await downloads.SaveTextAsync("codes.txt", "StudyAtrium — ä");

        Assert.Equal("StudyAtrium — ä", Encoding.UTF8.GetString(Assert.Single(downloads.Saved).Content));
        Assert.NotEqual(0xEF, downloads.Saved[0].Content[0]);
    }
}

public class BrowserFileDownloadServiceTests : ConsentBunitTest
{
    [Fact]
    public async Task SaveAsync_HandsNameAndBytesToTheBlobDownloadModule()
    {
        var module = Ctx.JSInterop.SetupModule("./js/file-download.js");
        var call = module.SetupVoid("saveFile", _ => true);
        call.SetVoidResult();
        await using var service = new BrowserFileDownloadService(Ctx.JSInterop.JSRuntime);

        await service.SaveAsync(new FileDownload("studyatrium-recovery-codes.txt", Encoding.UTF8.GetBytes("abc")));

        var invocation = Assert.Single(module.Invocations, i => i.Identifier == "saveFile");
        Assert.Equal("studyatrium-recovery-codes.txt", invocation.Arguments[0]);
        Assert.IsType<DotNetStreamReference>(invocation.Arguments[1]);
    }

    [Fact]
    public async Task SaveAsync_ImportsTheModuleOnce()
    {
        var module = Ctx.JSInterop.SetupModule("./js/file-download.js");
        module.SetupVoid("saveFile", _ => true).SetVoidResult();
        await using var service = new BrowserFileDownloadService(Ctx.JSInterop.JSRuntime);

        await service.SaveAsync(new FileDownload("a.txt", [1]));
        await service.SaveAsync(new FileDownload("b.txt", [2]));

        Assert.Single(Ctx.JSInterop.Invocations, i => i.Identifier == "import");
        Assert.Equal(2, module.Invocations.Count(i => i.Identifier == "saveFile"));
    }
}

/// <summary>Every download in the client goes through IFileDownloadService - the browser blob route
/// lives in exactly one place, so the native app's implementation covers every download.</summary>
public class ClientFileDownloadCallSiteTests
{
    // The only files allowed to build a browser download themselves.
    private static readonly string[] BrowserRoute =
    [
        Path.Combine("Services", "BrowserFileDownloadService.cs"),
        Path.Combine("wwwroot", "js", "file-download.js"),
    ];

    [Fact]
    public void NoComponentBuildsABrowserDownloadItself()
    {
        var clientDir = Path.Combine(FindRepoRoot(), "src", "StudyLife.Client");
        var markers = new[] { "createObjectURL", "DotNetStreamReference", "file-download.js", ".download =", "downloadFileFromStream" };
        var offenders = Directory.EnumerateFiles(clientDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".js"))
            .Where(f => !IsBuildOutput(clientDir, f))
            .Where(f => !BrowserRoute.Any(allowed => f.EndsWith(Path.DirectorySeparatorChar + allowed, StringComparison.Ordinal)))
            .Where(f => !f.Contains(Path.Combine("wwwroot", "lib")))
            .Where(f => markers.Any(File.ReadAllText(f).Contains))
            .Select(f => Path.GetRelativePath(clientDir, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Build the download through IFileDownloadService instead (the native app cannot run a blob download): "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData(typeof(SetupBackupCard))]
    [InlineData(typeof(SetupDeleteAccountCard))]
    [InlineData(typeof(PasskeyDeviceManager))]
    public void EveryDownloadingComponent_InjectsTheFileDownloadService(Type component)
    {
        var injected = component.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(p => p.PropertyType);
        Assert.Contains(typeof(IFileDownloadService), injected);
    }

    private static bool IsBuildOutput(string clientDir, string file)
    {
        var relative = Path.GetRelativePath(clientDir, file);
        return relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException($"Could not find StudyLife.sln above {AppContext.BaseDirectory}.");
    }
}

public class SetupBackupCardDownloadTests : ConsentBunitTest
{
    private readonly RecordingFileDownloads _downloads = new();

    private (IRenderedComponent<SetupBackupCard> Cut, ScriptedServer Server) Render(bool isOwner = false)
    {
        var server = new ScriptedServer();
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton<IFileDownloadService>(_downloads);
        var cut = Render<SetupBackupCard>(p => p
            .Add(c => c.IsOwner, isOwner)
            .Add(c => c.ExportJsonButtonText, "ExportJson")
            .Add(c => c.DownloadDatabaseButtonText, "DownloadDb")
            .Add(c => c.EncryptedDownloadErrorText, "DownloadFailed"));
        return (cut, server);
    }

    private static IRenderedComponent<SetupBackupCard> Click(IRenderedComponent<SetupBackupCard> cut, string text)
    {
        cut.FindAll("button").Single(b => b.TextContent.Trim() == text).Click();
        return cut;
    }

    [Fact]
    public void TheJsonExport_GoesThroughTheFileDownloadService()
    {
        var (cut, server) = Render();
        server.On("GET api/backup/export", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"formatVersion\":2}") });

        Click(cut, "ExportJson");

        cut.WaitForAssertion(() => Assert.Single(_downloads.Saved), ChainTimeout);
        Assert.Matches(@"^studyatrium-export-\d{8}\.json$", _downloads.Saved[0].FileName);
        Assert.Equal("{\"formatVersion\":2}", Encoding.UTF8.GetString(_downloads.Saved[0].Content));
        Assert.DoesNotContain("DownloadFailed", cut.Markup);
    }

    [Fact]
    public void TheDatabaseDownload_KeepsTheServersFileName()
    {
        var (cut, server) = Render(isOwner: true);
        server.On("GET api/backup/database", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 7]) };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "studyatrium-backup-20261116.db" };
            return response;
        });

        Click(cut, "DownloadDb");

        cut.WaitForAssertion(() => Assert.Equal("studyatrium-backup-20261116.db", Assert.Single(_downloads.Saved).FileName), ChainTimeout);
    }

    [Fact]
    public void TheEncryptedDatabaseDownload_PostsThePasswordAndOffersTheFile()
    {
        var (cut, server) = Render(isOwner: true);
        server.On("POST api/backup/database/encrypted", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        cut.Find("input[type=password]").Input("secret");

        Click(cut, "DownloadDb");

        cut.WaitForAssertion(() => Assert.EndsWith(".db.enc", Assert.Single(_downloads.Saved).FileName), ChainTimeout);
        Assert.Contains("secret", Assert.Single(server.Bodies));
    }

    [Fact]
    public void ADownloadTheDeviceCannotOffer_ShowsTheErrorText_InsteadOfDoingNothing()
    {
        _downloads.Failure = new InvalidOperationException("no sheet");
        var (cut, server) = Render();
        server.On("GET api/backup/export", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        Click(cut, "ExportJson");

        cut.WaitForAssertion(() => Assert.Contains("DownloadFailed", cut.Markup), ChainTimeout);
    }

    [Fact]
    public void AFailedRequest_ShowsTheErrorText_AndOffersNoFile()
    {
        var (cut, server) = Render();
        server.On("GET api/backup/export", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Click(cut, "ExportJson");

        cut.WaitForAssertion(() => Assert.Contains("DownloadFailed", cut.Markup), ChainTimeout);
        Assert.Empty(_downloads.Saved);
    }
}

public class PasskeyDeviceManagerRecoveryCodesDownloadTests : ConsentBunitTest
{
    private readonly RecordingFileDownloads _downloads = new();

    private IRenderedComponent<PasskeyDeviceManager> RenderWithFreshCodes()
    {
        var server = new ScriptedServer();
        server.OnJson("GET api/auth/credentials", new List<PasskeyListItemDto>());
        server.OnJson("GET api/auth/recovery/status", new RecoveryStatusDto());
        server.OnJson("POST api/auth/recovery/generate", new RecoveryCodesResponseDto { Codes = ["AAAA-1111", "BBBB-2222"] });
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new HttpClient(server) { BaseAddress = new Uri("https://studylife.test/") });
        Services.AddSingleton(new SessionTokenStore(new StubJSRuntime()));
        Services.AddSingleton<IFileDownloadService>(_downloads);
        Services.AddI18nText();
        var cut = Render<PasskeyDeviceManager>(p => p.Add(c => c.Open, true));
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "RecoveryCodesButton"), ChainTimeout);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "RecoveryCodesButton").Click();
        cut.Find(".modal-footer button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("AAAA-1111", cut.Markup), ChainTimeout);
        return cut;
    }

    [Fact]
    public void TheRecoveryCodesDownload_GoesThroughTheFileDownloadService()
    {
        var cut = RenderWithFreshCodes();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "RecoveryCodesDownload").Click();

        cut.WaitForAssertion(() => Assert.Single(_downloads.Saved), ChainTimeout);
        var file = _downloads.Saved[0];
        Assert.Equal("studyatrium-recovery-codes.txt", file.FileName);
        var text = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("AAAA-1111\nBBBB-2222", text);
    }

    [Fact]
    public void ARecoveryCodesDownloadTheDeviceCannotOffer_ShowsTheErrorText()
    {
        var cut = RenderWithFreshCodes();
        _downloads.Failure = new InvalidOperationException("no sheet");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "RecoveryCodesDownload").Click();

        cut.WaitForAssertion(() => Assert.Contains("PasskeysActionFailed", cut.Markup), ChainTimeout);
    }
}
