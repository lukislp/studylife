// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>Outcome of sending the telemetry answer to the server.</summary>
public enum TelemetryConsentSaveResult
{
    /// <summary>The server stored the answer.</summary>
    Saved,

    /// <summary>Not stored (offline, server error): the question may be asked again later.</summary>
    Failed,

    /// <summary>The server has no such endpoint (older than 3.38.0): the feature is unavailable.</summary>
    Unavailable,
}
