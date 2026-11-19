// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// GET /api/backup/export: the importable content of <see cref="BackupExportDto"/> plus the two
/// blocks the data subject is owed under GDPR Art. 15 (2026-11-17 audit F-9) - everything else
/// the server stores about the account (<see cref="Account"/>) and how it is processed
/// (<see cref="About"/>). Both are for reading only: POST /api/backup/import-json binds the same
/// file to <see cref="BackupExportDto"/>, which has neither property, so they are ignored there
/// and an import can never plant sign-in methods, sessions or consents.
/// Nothing in here is a secret: no password or key hashes, no tokens, no push endpoints or keys.
/// </summary>
public class PersonalDataExportDto : BackupExportDto
{
    public AccountExportDto Account { get; set; } = new();
    public ExportAboutDto About { get; set; } = new();
}

/// <summary>The account itself and everything attached to it besides study content.</summary>
public class AccountExportDto
{
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>The owner of the installation (may download the whole database).</summary>
    public bool IsOwner { get; set; }
    /// <summary>When the invite this account registered with was used; null without one.</summary>
    public DateTime? RegisteredWithInviteAt { get; set; }
    /// <summary>Null when the account has no password sign-in.</summary>
    public PasswordSignInExportDto? Password { get; set; }
    public List<PasskeyExportDto> Passkeys { get; set; } = new();
    public List<SignInSessionExportDto> SignInSessions { get; set; } = new();
    public RecoveryCodesExportDto RecoveryCodes { get; set; } = new();
    public List<PushDeviceExportDto> PushDevices { get; set; } = new();
    public AiConsentExportDto AiConsent { get; set; } = new();
    public TelemetryConsentExportDto TelemetryConsent { get; set; } = new();
    /// <summary>Access keys that currently exist (one per add-on slot, plus the calendar feed
    /// link). Which keys exist and since when - never the key.</summary>
    public List<AccessKeyExportDto> AccessKeys { get; set; } = new();
    public List<WebhookKeyExportDto> WebhookKeys { get; set; } = new();
    /// <summary>Apps the user granted access to through the connect flow.</summary>
    public List<ConnectedAppExportDto> ConnectedApps { get; set; } = new();
    /// <summary>Apps the user registered as a developer.</summary>
    public List<RegisteredAppExportDto> RegisteredApps { get; set; } = new();
    /// <summary>Invites the user created (owner only); who used one is another person's data
    /// and is left out.</summary>
    public List<InviteExportDto> InvitesCreated { get; set; } = new();
    /// <summary>Last focus-timer state the user's devices reported; null when none.</summary>
    public TimerStateExportDto? TimerState { get; set; }
    /// <summary>Notifications already sent, kept so none is sent twice.</summary>
    public List<SentNotificationExportDto> SentNotifications { get; set; } = new();
}

public class PasswordSignInExportDto
{
    public string Username { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int FailedAttemptCount { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public class PasskeyExportDto
{
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Null = still waiting for approval from a signed-in device.</summary>
    public DateTime? ApprovedAt { get; set; }
}

public class SignInSessionExportDto
{
    public DateTime IssuedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime HardExpiresAt { get; set; }
}

public class RecoveryCodesExportDto
{
    public int Remaining { get; set; }
}

public class PushDeviceExportDto
{
    /// <summary>"webpush" or "apns".</summary>
    public string Channel { get; set; } = "";
    /// <summary>Browser user agent (Web Push) or device name (APNs) as reported at registration.</summary>
    public string? Device { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class AiConsentExportDto
{
    public DateTime? GrantedAt { get; set; }
    /// <summary>Fingerprint of the AI provider description the consent was given for.</summary>
    public string? ProviderFingerprint { get; set; }
}

public class TelemetryConsentExportDto
{
    /// <summary>true = agreed, false = declined or withdrawn, null = never asked.</summary>
    public bool? Value { get; set; }
    /// <summary>When the answer was given; null for answers older than this record.</summary>
    public DateTime? AnsweredAt { get; set; }
    /// <summary>Version of the consent text answered.</summary>
    public int? TextVersion { get; set; }
    /// <summary>Every answer ever given, oldest first (the append-only consent log). Answers
    /// given before the log existed were copied into it by its migration.</summary>
    public List<TelemetryConsentLogEntryExportDto> History { get; set; } = [];
}

public class TelemetryConsentLogEntryExportDto
{
    /// <summary>true = agreed, false = declined or withdrawn.</summary>
    public bool Answer { get; set; }
    public DateTime AnsweredAt { get; set; }
    /// <summary>Version of the consent text answered.</summary>
    public int TextVersion { get; set; }
}

public class AccessKeyExportDto
{
    /// <summary>What the key is for: the API key slot ("ha", "ai", "mcp", "capture",
    /// "focusguard", "focustunes", "tray", "developer") or "calendar" for the calendar feed link.</summary>
    public string Purpose { get; set; } = "";
    public DateTime? CreatedAt { get; set; }
}

public class WebhookKeyExportDto
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ConnectedAppExportDto
{
    public string ClientId { get; set; } = "";
    /// <summary>Null when the app's registration no longer exists.</summary>
    public string? Name { get; set; }
    public List<string> GrantedScopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class RegisteredAppExportDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> RedirectUris { get; set; } = new();
    public List<string> RequestedScopes { get; set; } = new();
    /// <summary>"pending", "approved" or "rejected".</summary>
    public string Approval { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class InviteExportDto
{
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class TimerStateExportDto
{
    public int? SessionId { get; set; }
    public bool IsRunning { get; set; }
    public bool IsBreak { get; set; }
    public int CurrentRound { get; set; }
    public int TimerModeId { get; set; }
    public DateTime? PhaseEndsAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SentNotificationExportDto
{
    public string Key { get; set; } = "";
    public DateTime SentAt { get; set; }
}

/// <summary>
/// The information GDPR Art. 15(1)(a)-(h) adds to the copy of the data: purposes, categories,
/// recipients, retention, rights, complaint, source and automated decisions. Built from what the
/// server knows about its own configuration; it makes no claim about who operates it.
/// </summary>
public class ExportAboutDto
{
    public List<string> Purposes { get; set; } = new();
    public string Categories { get; set; } = "";
    public List<ExportRecipientDto> Recipients { get; set; } = new();
    public List<string> Retention { get; set; } = new();
    public List<string> Rights { get; set; } = new();
    public string Complaint { get; set; } = "";
    public string Source { get; set; } = "";
    public string AutomatedDecisions { get; set; } = "";
}

public class ExportRecipientDto
{
    public string Name { get; set; } = "";
    public string Receives { get; set; } = "";
}
