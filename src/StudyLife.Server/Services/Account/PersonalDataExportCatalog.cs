// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore.Metadata;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Where each kind of user data appears in the personal data export (GET /api/backup/export,
/// GDPR Art. 15) - the export's counterpart to <see cref="UserDataCatalog"/>, which answers the
/// same question for deletion. Every entity UserDataCatalog counts as user data must be either
/// <see cref="Exported"/> (with the JSON path that carries it) or <see cref="NotExported"/> (with
/// the reason). PersonalDataExportTests fails for an entity in neither list, so a new table of
/// user data cannot ship without a decision about the export.
/// </summary>
public static class PersonalDataExportCatalog
{
    /// <summary>Entity → JSON path (camelCase, dot-separated) in the export document. The test
    /// checks that the path is filled for a fully populated account.</summary>
    public static readonly IReadOnlyDictionary<Type, string> Exported = new Dictionary<Type, string>
    {
        [typeof(StudySessionEntity)] = "sessions",
        [typeof(NoteEntity)] = "notes",
        [typeof(CourseGoalEntity)] = "courseGoals",
        [typeof(CourseResourceEntity)] = "courseResources",
        [typeof(UserSettingsEntity)] = "settings",
        [typeof(StudyProgramEntity)] = "studyPrograms",
        [typeof(CourseGroupEntity)] = "courseGroups",
        [typeof(CustomCourseEntity)] = "customCourses",
        [typeof(SessionTemplateEntity)] = "sessionTemplates",
        [typeof(AuthUserEntity)] = "account.displayName",
        [typeof(PasswordCredentialEntity)] = "account.password",
        [typeof(PasskeyCredentialEntity)] = "account.passkeys",
        [typeof(AuthSessionEntity)] = "account.signInSessions",
        [typeof(RecoveryCodeEntity)] = "account.recoveryCodes",
        [typeof(PushSubscriptionEntity)] = "account.pushDevices",
        [typeof(WebhookApiKeyEntity)] = "account.webhookKeys",
        [typeof(ClientApiKeyEntity)] = "account.connectedApps",
        [typeof(OAuthClientEntity)] = "account.registeredApps",
        [typeof(AuthInviteEntity)] = "account.invitesCreated",
        [typeof(TimerStateEntity)] = "account.timerState",
        [typeof(SentReminderEntity)] = "account.sentNotifications",
        [typeof(TelemetryConsentLogEntity)] = "account.telemetryConsent.history",
    };

    /// <summary>User data deliberately left out of the export, each with the reason.</summary>
    public static readonly IReadOnlyDictionary<Type, string> NotExported = new Dictionary<Type, string>
    {
        [typeof(AiKeyOutboxEntity)] =
            "Delivery queue to the add-ons: a pending row can hold a freshly generated AI key in plaintext, and the rest " +
            "is retry bookkeeping. Whether an AI key exists is exported as account.accessKeys.",
    };

    /// <summary>User-data entities with no export decision - must be empty (see the guard test).</summary>
    public static IReadOnlyList<Type> Unclassified(IModel model) =>
        UserDataTypes(model)
            .Where(t => !Exported.ContainsKey(t) && !NotExported.ContainsKey(t))
            .ToList();

    /// <summary>Everything <see cref="UserDataCatalog"/> treats as a user's data: the generic
    /// per-user tables plus the explicitly handled ones.</summary>
    public static IEnumerable<Type> UserDataTypes(IModel model) =>
        UserDataCatalog.PerUserTables(model).Select(t => t.ClrType)
            .Concat(UserDataCatalog.Explicit.Keys.Where(t => model.FindEntityType(t) is not null))
            .Distinct();
}
