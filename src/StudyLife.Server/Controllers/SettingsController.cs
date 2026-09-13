using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly IDistributedCache _cache;
    private readonly ISettingsService _settings;
    private readonly IApiKeyService _apiKeys;

    public SettingsController(IDistributedCache cache, ISettingsService settings, IApiKeyService apiKeys)
    {
        _cache = cache;
        _settings = settings;
        _apiKeys = apiKeys;
    }

    [HttpGet]
    public async Task<ActionResult<UserSettingsDto>> Get()
    {
        var cacheKey = await _settings.CacheKeyAsync();
        var result = await _cache.GetOrSetAsync(this, cacheKey, SettingsService.CacheTtl, _settings.LoadAsync);

        // Audit finding A12b: ProgressShareToken is a bearer credential (it alone grants read
        // access to GET /api/progress/shared/{token}) and must only ever reach the browser's
        // own real passkey session, never an API-key caller (this endpoint is in the "ha" slot's
        // ApiKeyScopes, so Home Assistant CAN reach GET /api/settings for its own poll). Masking
        // is applied HERE, after the cache lookup above, rather than inside the cached factory:
        // the cache entry is keyed only by user+version (shared across auth types for the SAME
        // user - see CacheHelper), so a session request's cache miss would otherwise populate the
        // cache with the real token, and a same-user API-key request landing within that window
        // would receive it straight from cache without ever re-running the projection. Checking
        // result.Value (not the request's own success/failure) means a 304 short-circuit (no
        // body at all) is untouched - there's nothing to mask on an empty response. It also
        // stays in the controller rather than moving into SettingsService: which credential the
        // caller presented is an HTTP fact, read off HttpContext.Items.
        if (result.Value is not null && HttpContext.Items.ContainsKey(AuthSessionService.ApiKeySlotItemKey))
            result.Value.ProgressShareToken = null;

        return result;
    }

    [HttpPut]
    public async Task<ActionResult<UserSettingsDto>> Save(UserSettingsDto dto) =>
        (await _settings.SaveAsync(dto)).ToActionResult(this);

    /// <summary>
    /// Activates the read-only progress link (ProgressController.GetShared) and always
    /// generates a new token while doing so - Disable now also deletes the token (see there),
    /// so there's never an "old" token left that could be reused.
    /// </summary>
    // SessionOnly (audit finding A3 cleanup): a leaked API key must not be able to activate a
    // public read-only link to the account's data on its own, same rationale as the ha-api-key
    // group below. Disable/Regenerate below are intentionally left at the plain ApiAccess level
    // they already had - narrowing only the specific gap this refactor's audit called out
    // (activating a NEW public link) without changing the two endpoints nobody flagged.
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("progress-share/enable")]
    public async Task<ActionResult<UserSettingsDto>> EnableProgressShare() =>
        await _settings.EnableProgressShareAsync();

    /// <summary>
    /// Disables the link AND deletes the token (GET /api/progress/shared/{token} would
    /// respond with 404 afterward anyway, but the cleared token ensures that an already
    /// shared/leaked link doesn't become valid again just by re-enabling - anyone who has
    /// actually passed on the link and wants to protect against that should be able to rely
    /// on "Disable" alone, without necessarily having to know about the separate
    /// "Regenerate" button).
    /// </summary>
    [HttpPost("progress-share/disable")]
    public async Task<ActionResult<UserSettingsDto>> DisableProgressShare() =>
        (await _settings.DisableProgressShareAsync()).ToActionResult(this);

    /// <summary>
    /// Manual "regenerate now" (e.g. on suspicion of a leak, or to specifically invalidate a
    /// previously shared link) - immediately breaks any existing link, analogous to the calendar
    /// token (SystemController.RegenerateCalendarToken). Also enables the feature in the process
    /// (regenerating implies "I want a valid link again").
    /// </summary>
    [HttpPost("progress-share/regenerate")]
    public async Task<ActionResult<UserSettingsDto>> RegenerateProgressShareToken() =>
        (await _settings.RegenerateProgressShareTokenAsync()).ToActionResult(this);

    /// <summary>
    /// Hides the built-in study program from this user's switcher for good - see
    /// SettingsService.DismissBuiltInProgramAsync for why it needs a custom program to already
    /// exist and what happens to ActiveStudyProgramId.
    /// </summary>
    [HttpPost("builtin-program/dismiss")]
    public async Task<ActionResult<UserSettingsDto>> DismissBuiltInProgram() =>
        (await _settings.DismissBuiltInProgramAsync()).ToActionResult(this);

    // ── Single-key API slots (status / generate / revoke, once per integration) ────────────
    // Same endpoint pattern as progress-share/enable|disable|regenerate above (dedicated
    // POST write paths instead of the generic settings PUT), but with two peculiarities:
    // (1) The key lives on AuthUserEntity instead of UserSettingsEntity - it identifies the
    //     USER at the /api gate, long before settings are even resolved.
    // (2) All three endpoints require a REAL passkey session (SessionItemKey), not just
    //     any gate authentication: otherwise a leaked API key could reissue itself or
    //     revoke a user's key. That is also why every call below passes
    //     HttpContext.SessionAuthUserId() explicitly into ApiKeyService - the id exists
    //     BECAUSE of the policy on the action, so resolving it is the controller's job.
    //
    // Each integration below keeps its OWN route and its own request/response DTO types (the
    // committed OpenAPI contract in docs/api/openapi.json, which three consumer repos generate
    // clients from, names them one per slot), but the bodies are ApiKeyService's three generic
    // methods rather than eight near-identical copies - see ApiKeySlot for which column pair
    // each slot owns and why the token/hash handling is centralized. The two slots that do more
    // than write their columns (ai, developer: outbox + server-to-server registration) have
    // their own service methods.

    /// <summary>The session's user id - guaranteed present by [Authorize(SessionOnly)] on every
    /// action that calls this.</summary>
    private int SessionUserId => HttpContext.SessionAuthUserId()!.Value;

    // ── Per-user API key for Home Assistant (phase 3) ──────────────────────

    /// <summary>Status for the setup card: does a key exist, and since when? Deliberately NO
    /// plaintext access - the key, like a password, is only visible once at generation.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("ha-api-key")]
    public async Task<ActionResult<HaApiKeyStatusDto>> GetHaApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToHaApiKeyStatusDto)).ToActionResult(this);

    /// <summary>
    /// Generates a new long-lived per-user API key (immediately replaces any existing one -
    /// the old hash is overwritten, the old key gets 401 from now on). The PLAINTEXT is
    /// returned exactly once in this response; only the SHA-256 hash is stored (same pattern
    /// as AuthSessionService.IssueSession). No rotation, no expiry - an explicit user
    /// decision ("long lived"), because Home Assistant has no live session that could react
    /// to a rotation.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ha-api-key/generate")]
    public async Task<ActionResult<HaApiKeyGenerateResponseDto>> GenerateHaApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.Ha,
            (key, createdAt) => new HaApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    /// <summary>Permanently revokes the per-user API key (hash is deleted) - Home Assistant
    /// gets 401 from the next request onward and shows its reauth flow.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ha-api-key/revoke")]
    public async Task<IActionResult> RevokeHaApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.Ha)).ToNoContentResult(this);

    // Same three-endpoint shape as the ha-api-key group above, for the separate studylife-ai
    // key slot (AuthUserEntity.AiApiKeyHash) - the only slot whose generate/revoke do more than
    // write their own two columns (outbox + server-to-server registration, see ApiKeyService).

    /// <summary>Status for the setup card: does an AI-integration key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("ai-api-key")]
    public async Task<ActionResult<AiApiKeyStatusDto>> GetAiApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToAiApiKeyStatusDto)).ToActionResult(this);

    /// <summary>Generates a new long-lived per-user API key for studylife-ai (this slot only -
    /// Home Assistant's key is untouched) and registers the plaintext with studylife-ai at the
    /// one moment it exists - see ApiKeyService.GenerateAiKeyAsync for the outbox contract.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ai-api-key/generate")]
    public async Task<ActionResult<AiApiKeyGenerateResponseDto>> GenerateAiApiKey(CancellationToken ct) =>
        (await _apiKeys.GenerateAiKeyAsync(SessionUserId, ct)).ToActionResult(this);

    /// <summary>Permanently revokes the studylife-ai API key (hash is deleted) - studylife-ai
    /// gets 401 from the next request onward, and is told to forget its registered copy. Home
    /// Assistant's key is untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ai-api-key/revoke")]
    public async Task<IActionResult> RevokeAiApiKey(CancellationToken ct) =>
        (await _apiKeys.RevokeAiKeyAsync(SessionUserId, ct)).ToNoContentResult(this);

    // Same three-endpoint shape again, for the separate studylife-mcp key slot
    // (AuthUserEntity.McpApiKeyHash). Unlike the ai-api-key group above, there is no
    // server-to-server registration call here: studylife-mcp is a locally-run MCP server (like
    // Home Assistant, not like the hosted studylife-ai microservice) that the user configures
    // with the plaintext key themselves - this backend never needs to hand it to anyone.

    /// <summary>Status for the setup card: does an MCP-integration key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("mcp-api-key")]
    public async Task<ActionResult<McpApiKeyStatusDto>> GetMcpApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToMcpApiKeyStatusDto)).ToActionResult(this);

    /// <summary>Generates a new long-lived per-user API key for studylife-mcp (immediately
    /// replaces any existing one in this slot only - ApiKeyHash/AiApiKeyHash are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("mcp-api-key/generate")]
    public async Task<ActionResult<McpApiKeyGenerateResponseDto>> GenerateMcpApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.Mcp,
            (key, createdAt) => new McpApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    /// <summary>Permanently revokes the studylife-mcp API key (hash is deleted) - studylife-mcp
    /// gets 401 from the next request onward. The other two slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("mcp-api-key/revoke")]
    public async Task<IActionResult> RevokeMcpApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.Mcp)).ToNoContentResult(this);

    // Same three-endpoint shape again, for the separate studylife-capture browser-extension key
    // slot (AuthUserEntity.CaptureApiKeyHash). Like the mcp-api-key group (and unlike ai-api-key),
    // there is no server-to-server registration call here - the extension holds the plaintext
    // key itself (pasted into its own settings popup) and sends it directly as X-Api-Key on
    // every request, resolved by the same gate middleware every other key type goes through.

    /// <summary>Status for the setup card: does a Capture-extension key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("capture-api-key")]
    public async Task<ActionResult<CaptureApiKeyStatusDto>> GetCaptureApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToCaptureApiKeyStatusDto)).ToActionResult(this);

    /// <summary>Generates a new long-lived per-user API key for studylife-capture (immediately
    /// replaces any existing one in this slot only - the other three slots are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("capture-api-key/generate")]
    public async Task<ActionResult<CaptureApiKeyGenerateResponseDto>> GenerateCaptureApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.Capture,
            (key, createdAt) => new CaptureApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    /// <summary>Permanently revokes the studylife-capture API key (hash is deleted) - the
    /// extension gets 401 from the next request onward. The other three slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("capture-api-key/revoke")]
    public async Task<IActionResult> RevokeCaptureApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.Capture)).ToNoContentResult(this);

    // Same two-endpoint shape again (status + revoke, no generate), for the separate
    // studylife-focusguard browser-extension key slot (AuthUserEntity.FocusGuardApiKeyHash).
    // Like capture/mcp, provisioning happens exclusively through the consent flow
    // (AuthController.FocusGuardConnect), never a plaintext-paste here.

    /// <summary>Status for the setup card: does a FocusGuard key exist, and since when? Same
    /// "never the plaintext" rule as GetCaptureApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("focusguard-api-key")]
    public async Task<ActionResult<FocusGuardApiKeyStatusDto>> GetFocusGuardApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToFocusGuardApiKeyStatusDto)).ToActionResult(this);

    /// <summary>Generates a new long-lived per-user API key for studylife-focusguard (immediately
    /// replaces any existing one in this slot only). Not the extension's actual path (it uses the
    /// consent flow, AuthController.FocusGuardConnect) - kept for the same uniform admin/test
    /// surface every other slot has (see GenerateCaptureApiKey).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focusguard-api-key/generate")]
    public async Task<ActionResult<FocusGuardApiKeyGenerateResponseDto>> GenerateFocusGuardApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.FocusGuard,
            (key, createdAt) => new FocusGuardApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    /// <summary>Permanently revokes the studylife-focusguard API key (hash is deleted) - the
    /// extension gets 401 from the next poll onward. The other four slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focusguard-api-key/revoke")]
    public async Task<IActionResult> RevokeFocusGuardApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.FocusGuard)).ToNoContentResult(this);

    // Same three-endpoint shape again, for the separate studylife-focustunes browser-extension
    // key slot (AuthUserEntity.FocusTunesApiKeyHash). Provisioning is via the consent flow
    // (AuthController.FocusTunesConnect); generate/revoke exist for the same uniform admin/test
    // surface every other slot has.

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("focustunes-api-key")]
    public async Task<ActionResult<FocusTunesApiKeyStatusDto>> GetFocusTunesApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToFocusTunesApiKeyStatusDto)).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focustunes-api-key/generate")]
    public async Task<ActionResult<FocusTunesApiKeyGenerateResponseDto>> GenerateFocusTunesApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.FocusTunes,
            (key, createdAt) => new FocusTunesApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focustunes-api-key/revoke")]
    public async Task<IActionResult> RevokeFocusTunesApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.FocusTunes)).ToNoContentResult(this);

    // Same three-endpoint shape again, for the separate studylife-tray desktop-app key slot
    // (AuthUserEntity.TrayApiKeyHash). Provisioning is via the consent flow
    // (AuthController.TrayConnect); generate/revoke exist for the same uniform admin/test
    // surface every other slot has.

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("tray-api-key")]
    public async Task<ActionResult<TrayApiKeyStatusDto>> GetTrayApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToTrayApiKeyStatusDto)).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("tray-api-key/generate")]
    public async Task<ActionResult<TrayApiKeyGenerateResponseDto>> GenerateTrayApiKey() =>
        (await _apiKeys.GenerateAsync(SessionUserId, ApiKeySlots.Tray,
            (key, createdAt) => new TrayApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = createdAt })).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("tray-api-key/revoke")]
    public async Task<IActionResult> RevokeTrayApiKey() =>
        (await _apiKeys.RevokeAsync(SessionUserId, ApiKeySlots.Tray)).ToNoContentResult(this);

    // Unlike every other slot above (one key per user, a column on AuthUserEntity), the
    // studylife-webhooks registration-management slot supports multiple NAMED keys per user
    // (WebhookApiKeyEntity) - one per external program/add-on the user wants to let manage its
    // own webhook subscriptions (see ApiKeyScopes.Webhooks), not one single known consumer.

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("webhooks-api-keys")]
    public async Task<ActionResult<List<WebhookApiKeyDto>>> GetWebhooksApiKeys() =>
        await _apiKeys.GetWebhookKeysAsync(SessionUserId);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("webhooks-api-keys")]
    public async Task<ActionResult<CreateWebhookApiKeyResponseDto>> CreateWebhooksApiKey(CreateWebhookApiKeyRequestDto request) =>
        (await _apiKeys.CreateWebhookKeyAsync(SessionUserId, request)).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("webhooks-api-keys/{id}")]
    public async Task<IActionResult> DeleteWebhooksApiKey(int id) =>
        (await _apiKeys.DeleteWebhookKeyAsync(SessionUserId, id)).ToNoContentResult(this);

    // Same three-endpoint shape as the ai-api-key group above, for the separate
    // studylife-developers portal key slot (AuthUserEntity.DeveloperApiKeyHash) - a toggle,
    // not a reveal-once-plaintext lifecycle: nothing external ever needs the raw key, the
    // server hands it to studylife-developers itself. Deliberately no outbox here (see
    // DeveloperProxyClient's own class doc) - a failed delivery simply surfaces as a
    // non-success response, the user retries the toggle.

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("developer-api-key")]
    public async Task<ActionResult<DeveloperApiKeyStatusDto>> GetDeveloperApiKeyStatus() =>
        (await _apiKeys.GetStatusAsync(SessionUserId, ApiKeyService.ToDeveloperApiKeyStatusDto)).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("developer-api-key/generate")]
    public async Task<ActionResult<DeveloperApiKeyGenerateResponseDto>> GenerateDeveloperApiKey(CancellationToken ct) =>
        (await _apiKeys.GenerateDeveloperKeyAsync(SessionUserId, ct)).ToActionResult(this);

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("developer-api-key/revoke")]
    public async Task<IActionResult> RevokeDeveloperApiKey(CancellationToken ct) =>
        (await _apiKeys.RevokeDeveloperKeyAsync(SessionUserId, ct)).ToNoContentResult(this);
}
