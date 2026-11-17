// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// The one way the client hands a file to the user: the backup and JSON export, the export before
/// deleting an account and the recovery codes all go through it. The browser registers
/// <see cref="BrowserFileDownloadService"/> (a blob download); the native app (studylife-app)
/// registers its own implementation, because a BlazorWebView has no download manager - on iOS the
/// WebView cancels the blob navigation and nothing happens - and offers the file through the
/// system's save/share sheet instead. Components never touch blob URLs or the share sheet
/// themselves (ClientFileDownloadCallSiteTests enforces that).
/// </summary>
public interface IFileDownloadService
{
    /// <summary>Offers <paramref name="file"/> to the user. Returns once the file was handed over:
    /// the browser download started, or the native save/share sheet or save dialog was closed -
    /// saved, shared or cancelled. Cancelling is the user's choice, not a failure. Throws only when
    /// the file could not be offered or saving it failed, so the caller shows its error text only
    /// then and never does nothing.</summary>
    Task SaveAsync(FileDownload file, CancellationToken cancellationToken = default);
}

/// <summary>A file to hand to the user. <see cref="FileName"/> is always a bare, safe file name
/// (see <see cref="FileDownloadName.Sanitize"/>), so an implementation may use it as a path
/// segment on the device.</summary>
public sealed record FileDownload
{
    public FileDownload(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        FileName = FileDownloadName.Sanitize(fileName);
        Content = content;
    }

    public string FileName { get; }
    public byte[] Content { get; }
}

/// <summary>File names for <see cref="FileDownload"/>. A name can come from the server
/// (Content-Disposition), and the native app writes the file to its cache directory under that
/// name, so it is reduced to a single, harmless path segment here, once, for every implementation.</summary>
public static class FileDownloadName
{
    public const string Fallback = "download";
    private const int MaxLength = 200;
    private const string ReservedChars = ":*?\"<>|";

    public static string Sanitize(string? fileName)
    {
        var name = (fileName ?? "").Trim().Trim('"').Trim();
        // Keep only the last segment of anything that looks like a path (either separator,
        // whatever the platform this runs on).
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];
        // Characters no file system on any target platform accepts (the WASM runtime's
        // Path.GetInvalidFileNameChars() only knows '/' and NUL, so the list is spelled out).
        var chars = name.Select(c => char.IsControl(c) || ReservedChars.Contains(c) ? '_' : c);
        name = new string(chars.ToArray()).Trim().TrimStart('.').Trim();
        if (name.Length > MaxLength) name = name[..MaxLength];
        return name.Length == 0 ? Fallback : name;
    }
}

public static class FileDownloadServiceExtensions
{
    /// <summary>Offers the body of a successful HTTP response as a file: the name from its
    /// Content-Disposition header when the server sent one, otherwise <paramref name="fallbackFileName"/>.</summary>
    public static async Task SaveResponseAsync(this IFileDownloadService downloads, HttpResponseMessage response,
        string fallbackFileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName ?? fallbackFileName;
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await downloads.SaveAsync(new FileDownload(fileName, content), cancellationToken);
    }

    /// <summary>Offers a text file (UTF-8, no byte order mark).</summary>
    public static Task SaveTextAsync(this IFileDownloadService downloads, string fileName, string content,
        CancellationToken cancellationToken = default) =>
        downloads.SaveAsync(new FileDownload(fileName, System.Text.Encoding.UTF8.GetBytes(content)), cancellationToken);
}
