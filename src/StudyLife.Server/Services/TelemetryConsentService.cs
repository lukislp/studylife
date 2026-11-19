// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// The user's answer to the telemetry question - the only writer of
/// <see cref="UserSettingsEntity.TelemetryConsent"/>. It is deliberately not part of the generic
/// settings PUT: that endpoint is reachable with API keys (for example the Home Assistant key), and
/// a consent a user never gave on a real session must not be changeable that way - least of all a
/// withdrawal being turned back into a grant. The controller makes the endpoint session-only; this
/// service owns the rule.
/// </summary>
public interface ITelemetryConsentService
{
    /// <summary>Records <paramref name="answer"/> (true = agreed, false = declined or withdrawn).
    /// Appends to the consent log and updates the current answer; a repeat of the stored answer to
    /// the same text version changes nothing.</summary>
    Task<ServiceResult> SetAsync(bool answer, CancellationToken ct = default);
}

public class TelemetryConsentService(
    StudyLifeDb db,
    SettingsCacheVersion settingsCacheVersion,
    ICurrentUserAccessor currentUser) : ITelemetryConsentService
{
    public async Task<ServiceResult> SetAsync(bool answer, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var settings = await db.Settings.GetOrCreateAsync(db);
        if (!TelemetryConsentRecord.Apply(settings, answer, now)) return ServiceResult.Success();

        // Append-only: the log is never updated or pruned (it goes with the account on deletion),
        // so every answer, with the text version it answered, stays provable (GDPR Art. 7(1)).
        db.TelemetryConsentLog.Add(new TelemetryConsentLogEntity
        {
            AuthUserId = currentUser.AuthUserId,
            Answer = answer,
            AnsweredAt = now,
            TextVersion = TelemetryConsentRecord.CurrentVersion,
        });
        await db.SaveChangesAsync(ct);
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        return ServiceResult.Success();
    }
}