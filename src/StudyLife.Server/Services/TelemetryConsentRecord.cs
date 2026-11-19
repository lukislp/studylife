// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// The one rule for changing a user's telemetry answer (2026-11-17 GDPR audit F-8): whoever
/// changes <see cref="UserSettingsEntity.TelemetryConsent"/> goes through <see cref="Apply"/>, so
/// the current answer is always stored together with when it was given and which consent text it
/// answered (Art. 7(1): the controller must be able to show the consent). The history of answers
/// is the append-only <see cref="TelemetryConsentLogEntity"/> log, written by
/// <see cref="TelemetryConsentService"/>. Null keeps its meaning "never asked": it carries no
/// timestamp and no version, and no endpoint writes it any more - a user can only answer.
/// </summary>
public static class TelemetryConsentRecord
{
    /// <summary>
    /// Version of the consent text the user answers: the client's TelemetryConsentModal and the
    /// Setup telemetry card (i18ntext/TelemetryConsentModalText.*.json, SetupText Telemetry*).
    /// Bump it when what those texts say is collected, or why, or who receives it, changes - a
    /// stored answer then shows it was given to the older text.
    /// Version 1: the texts as of 2026-11-17.
    /// Version 2: the texts name the recipient (the operator of the user's own StudyAtrium server)
    /// instead of an unspecified "we" (2026-11-19 App Store audit S1).
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Stores <paramref name="answer"/> on <paramref name="settings"/>, stamped with
    /// <paramref name="utcNow"/> and <see cref="CurrentVersion"/>. Returns false, and changes
    /// nothing, when the stored answer is the same answer to the same text version - the original
    /// timestamp is the proof, a repeated identical answer must not move it.</summary>
    public static bool Apply(UserSettingsEntity settings, bool answer, DateTime utcNow)
    {
        if (settings.TelemetryConsent == answer && settings.TelemetryConsentVersion == CurrentVersion)
            return false;

        settings.TelemetryConsent = answer;
        settings.TelemetryConsentAt = utcNow;
        settings.TelemetryConsentVersion = CurrentVersion;
        return true;
    }
}
