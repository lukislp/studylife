// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// The one rule for changing a user's telemetry answer (2026-11-17 GDPR audit F-8): whoever
/// changes <see cref="UserSettingsEntity.TelemetryConsent"/> goes through <see cref="Apply"/>, so
/// every grant, decline and withdrawal is stored together with when it happened and which consent
/// text it answered (Art. 7(1): the controller must be able to show the consent). Null keeps its
/// meaning "never asked", so it carries no timestamp and no version either.
/// </summary>
public static class TelemetryConsentRecord
{
    /// <summary>
    /// Version of the consent text the user answers: the client's TelemetryConsentModal and the
    /// Setup telemetry card (i18ntext/TelemetryConsentModalText.*.json, SetupText Telemetry*).
    /// Bump it when what those texts say is collected, or why, changes - a stored answer then
    /// shows it was given to the older text.
    /// Version 1: the texts as of 2026-11-17.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Stores <paramref name="answer"/> on <paramref name="settings"/>. An unchanged
    /// answer keeps the original timestamp and version; a changed one is stamped with
    /// <paramref name="utcNow"/> and <see cref="CurrentVersion"/>; null clears both.</summary>
    public static void Apply(UserSettingsEntity settings, bool? answer, DateTime utcNow)
    {
        if (settings.TelemetryConsent == answer) return;

        settings.TelemetryConsent = answer;
        settings.TelemetryConsentAt = answer is null ? null : utcNow;
        settings.TelemetryConsentVersion = answer is null ? null : CurrentVersion;
    }
}
