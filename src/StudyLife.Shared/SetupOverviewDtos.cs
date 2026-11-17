// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Shared;

/// <summary>
/// Server-side bundle of the ~14 read-only GETs the Setup page and its cards fire on every
/// open (settings, capabilities, version, calendar token, study programs, course goals, eight
/// per-integration key statuses, webhook API keys, client keys, invites, restore status) - same
/// motivation as DashboardSummaryDto, but there is no shared computation here: every section is
/// just the exact response one of those endpoints would already give this caller, assembled in
/// one round trip instead of many. GET api/webhooks (WebhooksProxyController) is deliberately
/// NOT included - it proxies to the external studylife-webhooks microservice, so bundling it
/// would add a network hop to every Setup page load instead of removing one; the subscriptions
/// card keeps fetching it directly.
///
/// Every section is nullable and reproduces its endpoint's response 1:1 for THIS caller,
/// including access control: a section the individual endpoint would deny (401/403/404/503 -
/// not the owner, demo restrictions, no such user) comes back null rather than any data the
/// caller could not already get today, and the corresponding card/page fetch then falls back to
/// its own request exactly as before.
/// </summary>
public class SetupOverviewDto
{
    public UserSettingsDto? Settings { get; set; }
    public SystemCapabilitiesResponseDto? Capabilities { get; set; }
    public VersionResponseDto? Version { get; set; }

    /// <summary>Same as GET api/system/calendar-token: whether a calendar link exists. Never the
    /// token itself - it is stored only as a hash (audit 2026-11-16 L5). Servers before that
    /// sent a plaintext <c>CalendarToken</c> field here instead; a client that finds this
    /// section null asks the endpoint, which such a server answers with the plaintext.</summary>
    public CalendarTokenResponseDto? CalendarLink { get; set; }

    public List<StudyProgramSummaryDto>? StudyPrograms { get; set; }
    public List<CourseGoalDto>? CourseGoals { get; set; }

    public HaApiKeyStatusDto? HaApiKey { get; set; }
    public AiApiKeyStatusDto? AiApiKey { get; set; }
    public McpApiKeyStatusDto? McpApiKey { get; set; }
    public CaptureApiKeyStatusDto? CaptureApiKey { get; set; }
    public FocusGuardApiKeyStatusDto? FocusGuardApiKey { get; set; }
    public FocusTunesApiKeyStatusDto? FocusTunesApiKey { get; set; }
    public TrayApiKeyStatusDto? TrayApiKey { get; set; }
    public DeveloperApiKeyStatusDto? DeveloperApiKey { get; set; }

    public List<WebhookApiKeyDto>? WebhookApiKeys { get; set; }
    public List<ClientApiKeyListItemDto>? ClientKeys { get; set; }

    /// <summary>Owner-only (AuthController.ListInvites) - null for a non-owner session, same as
    /// the 403 that endpoint would give.</summary>
    public List<InviteListItemDto>? Invites { get; set; }

    /// <summary>Owner-only AND raw-backup-only AND blocked entirely on a demo instance
    /// (Program.cs's /api/backup write-block middleware covers every method, GETs included) -
    /// null whenever any of those would make BackupController.RestoreStatus answer anything but
    /// 200 for this caller.</summary>
    public RestoreStatusResponseDto? RestoreStatus { get; set; }

    /// <summary>Same source as GET api/auth/account-info - always populated, every session user
    /// can see their own owner flag.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Same source as GET api/auth/demo.</summary>
    public bool IsDemo { get; set; }
}
