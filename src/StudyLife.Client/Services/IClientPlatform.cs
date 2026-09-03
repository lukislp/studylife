namespace StudyLife.Client.Services;

/// <summary>
/// Tells TelemetryService which of the contract's fixed platform enumeration (web/ios/android/
/// windows/maccatalyst) this running client is - same additive-hook pattern as INativeHealthData/
/// INativePush: the browser registers <see cref="BrowserClientPlatform"/> ("web"), the native
/// app shell (separate studylife-app repo) registers its own implementation returning the real
/// OS, exactly like it already overrides INativeAppAuth/INativeHealthData/INativePush.
/// </summary>
public interface IClientPlatform
{
    string Name { get; }
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class BrowserClientPlatform : IClientPlatform
{
    public string Name => "web";
}
