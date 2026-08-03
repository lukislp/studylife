namespace StudyLife.Client.Services;

/// <summary>
/// Additive hook for "save file on the device" (same pattern as INativeAppAuth/
/// INativePush): in the browser the download goes through the blob route (JS, like SetupBackupCard) -
/// in the native app's WebView that doesn't work (no download manager), so there the
/// app shell instead opens the system share/save sheet with the file.
/// First consumer: the recovery codes download in PasskeyDeviceManager.
/// </summary>
public interface INativeFileExport
{
    bool IsAvailable => false;

    /// <summary>Saves/shares a text file via the operating system's native mechanism.</summary>
    Task SaveTextAsync(string fileName, string content) => Task.CompletedTask;
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeFileExport : INativeFileExport
{
}
