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
/// its own request exactly as before. CalendarToken is the one write-avoidance case: the real
/// endpoint lazily GENERATES a token on first fetch (a GET with a side effect - see
/// SystemController.GetCalendarToken), which this bundle must never do, so it only ever reports
/// an ALREADY existing token; a user without one yet sees null here and the page falls back to
/// the real endpoint, same as any other missing section.
/// </summary>
public class SetupOverviewDto
{
    public UserSettingsDto? Settings { get; set; }
    public SystemCapabilitiesResponseDto? Capabilities { get; set; }
    public VersionResponseDto? Version { get; set; }

    /// <summary>Only set when a calendar token already exists - see class summary. Null means
    /// "ask GET api/system/calendar-token", not "the token is empty".</summary>
    public string? CalendarToken { get; set; }

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
