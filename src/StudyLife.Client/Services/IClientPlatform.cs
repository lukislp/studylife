// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

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

    /// <summary>
    /// Display version of the native app shell (e.g. "1.9.5"), null in the browser and in app
    /// builds that do not report it. The compiled-in client carries no release stamp of its own
    /// there, so this is the version that identifies the build (About section in Setup).
    /// Default so the browser registration and older shells keep working.
    /// </summary>
    string? AppVersion => null;

    /// <summary>
    /// The THIRD-PARTY-NOTICES.md that ships inside the native app bundle, or null when this
    /// platform has none (the browser, older shells): the About section then falls back to the
    /// notices the server publishes. Default for the same reason as <see cref="AppVersion"/>.
    /// </summary>
    Task<string?> ReadBundledNoticesAsync() => Task.FromResult<string?>(null);

    /// <summary>
    /// True when the UI language comes from the operating system's language setting for this app
    /// (iOS/iPadOS/macOS: Settings, StudyAtrium, Language; Android 13+: per-app language; Windows: the
    /// system language). The shared UI then shows no language picker but a hint with
    /// <see cref="OpenLanguageSettingsAsync"/> instead - this flag is the one place that decides it.
    /// False in the browser, where the picker stays. Default so older shells keep the picker.
    /// </summary>
    bool LanguageFromSystem => false;

    /// <summary>Opens the operating system's language setting for this app (or its settings page
    /// where the system has no per-app language page). Only used when <see cref="LanguageFromSystem"/>.</summary>
    Task OpenLanguageSettingsAsync() => Task.CompletedTask;

    /// <summary>
    /// Whether Setup offers the developer-oriented integration cards: the add-on registration slot
    /// (studylife-developers) and the marketplace browser, which reads a remote add-on catalogue.
    /// True in the browser. The Apple store shells return false: they must not present a plug-in
    /// store or load remote catalogue content that can change after review. Default so the
    /// browser registration and older shells keep the cards.
    /// </summary>
    bool ShowsDeveloperIntegrations => true;
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class BrowserClientPlatform : IClientPlatform
{
    public string Name => "web";
}
