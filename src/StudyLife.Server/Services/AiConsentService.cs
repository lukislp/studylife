// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The one place that decides whether a user's notes, courses, sessions and goals may go to the
/// studylife-ai add-on (and from there to an AI provider). Apple's App Review guideline 5.1.2(i)
/// requires disclosure and explicit permission before personal data is shared with a third
/// party, including third-party AI; every path that sends user data asks THIS service:
/// key generation (<see cref="ApiKeyService"/>, where ingestion starts), the three proxy
/// endpoints (<c>AiProxyController</c>) and capture enrichment
/// (<c>BackgroundTaskService.CaptureEnrichment</c>). Controllers only map the outcome to HTTP.
///
/// The rule: consent is required whenever the provider description reports an external provider
/// (<c>AnyExternal</c>) or there is no description (studylife-ai unreachable or older than its
/// /internal/provider-info endpoint) - an unknown provider is treated as external, never as
/// "nothing to ask". With only local models no consent is required. A consent covers the exact
/// provider description it was given for (<see cref="AiConsentService.Fingerprint"/>); when the
/// operator changes provider or model the stored consent no longer matches, requests are refused
/// and the user is asked again. Without a configured add-on nothing can leave the server, so
/// nothing is asked.
///
/// A stored consent is never written through the settings PUT, only by <c>GrantAsync</c>.
/// Withdrawing clears it and revokes the AI key through the existing revoke path, which makes
/// studylife-ai purge the user's indexed data.
/// </summary>
public interface IAiConsentService
{
    /// <summary>Everything the client needs to decide whether and what to ask.</summary>
    Task<AiConsentStatusDto> GetStatusAsync(int userId, CancellationToken ct);

    /// <summary>Records the user's consent for the provider description they were shown
    /// (<paramref name="fingerprint"/>). Invalid for a malformed fingerprint or an installation
    /// without AI; Conflict (carrying the current status) when the description changed since the
    /// user was shown it.</summary>
    Task<ServiceResult<AiConsentStatusDto>> GrantAsync(int userId, string fingerprint, CancellationToken ct);

    /// <summary>Clears the consent and revokes the user's AI key (which purges their data in
    /// studylife-ai). Also what "not now" means for an existing AI user who is asked at app
    /// start.</summary>
    Task<ServiceResult> WithdrawAsync(int userId, CancellationToken ct);

    /// <summary>The gate for every path that sends user data to studylife-ai: Success, or
    /// Forbidden with the problem code <see cref="AiConsentService.ConsentRequiredProblem"/>.</summary>
    Task<ServiceResult> EnsureGrantedAsync(int userId, CancellationToken ct);

    /// <summary>Background variant of the gate: the user has switched AI on (has a key) AND may
    /// send data. Capture enrichment must skip everyone else.</summary>
    Task<bool> MayProcessInBackgroundAsync(int userId, CancellationToken ct);

    /// <summary>The AI part of GET /api/system/capabilities.</summary>
    Task<AiCapabilities> GetCapabilitiesAsync(CancellationToken ct);
}

/// <summary>Whether the add-on is usable here and which providers it uses (null = unknown).</summary>
public sealed record AiCapabilities(bool Available, AiProviderInfoDto? Provider);

public sealed class AiConsentService : IAiConsentService
{
    /// <summary>Stable problem code of the 403 returned while consent is missing; the client
    /// reacts to it by showing the consent dialog.</summary>
    public const string ConsentRequiredProblem = "ai-consent-required";

    private readonly StudyLifeDb _db;
    private readonly AiProxyClient _client;
    private readonly bool _demo;
    // Resolved only when a withdrawal has to revoke the key. A factory instead of an
    // IApiKeyService parameter because ApiKeyService itself depends on this service (its key
    // generation is gated here), and a plain constructor dependency would be a cycle.
    private readonly Func<IApiKeyService>? _apiKeys;

    public AiConsentService(StudyLifeDb db, AiProxyClient client, bool demo, Func<IApiKeyService>? apiKeys = null)
    {
        _db = db;
        _client = client;
        _demo = demo;
        _apiKeys = apiKeys;
    }

    private readonly record struct Decision(bool Available, AiProviderInfo? Info, bool Required, string Fingerprint);

    private async Task<Decision> DecideAsync(CancellationToken ct)
    {
        var available = _client.Enabled && !_demo;
        var info = available ? await _client.GetProviderInfoAsync(ct) : null;
        // Unknown (info null) counts as external: never skip consent because the description
        // is missing.
        var required = available && (info is null || info.AnyExternal);
        return new Decision(available, info, required, Fingerprint(info));
    }

    private static bool Covers(UserSettingsEntity? settings, Decision decision) =>
        settings is { AiConsentGrantedAt: not null }
        && string.Equals(settings.AiConsentProviderFingerprint, decision.Fingerprint, StringComparison.Ordinal);

    /// <summary>Hash of the provider description a consent is given for: every role's name,
    /// provider label, model and locality, order-independent. Null (unknown description) has its
    /// own fixed value, so a consent given for "unknown provider" does not silently carry over
    /// to a real description once studylife-ai reports one.</summary>
    public static string Fingerprint(AiProviderInfo? info)
    {
        var canonical = info is null
            ? "unknown"
            : string.Join('\n', info.Roles
                .Select(r => $"{r.Role}|{r.Provider}|{r.Model}|{(r.Local ? 1 : 0)}")
                .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public async Task<AiConsentStatusDto> GetStatusAsync(int userId, CancellationToken ct)
    {
        var decision = await DecideAsync(ct);
        var settings = await ReadSettingsAsync(userId, ct);
        var hasKey = await HasKeyAsync(userId, ct);
        return ToStatus(decision, settings, hasKey);
    }

    private static AiConsentStatusDto ToStatus(Decision decision, UserSettingsEntity? settings, bool hasKey)
    {
        var covers = Covers(settings, decision);
        return new AiConsentStatusDto
        {
            AiAvailable = decision.Available,
            ConsentRequired = decision.Required,
            Granted = covers,
            GrantedAt = covers ? settings!.AiConsentGrantedAt : null,
            HasKey = hasKey,
            ProviderKnown = decision.Info is not null,
            Provider = ToDto(decision.Info),
            Fingerprint = decision.Fingerprint,
            NeedsDecision = decision.Available && decision.Required && !covers && hasKey,
        };
    }

    public async Task<ServiceResult<AiConsentStatusDto>> GrantAsync(int userId, string fingerprint, CancellationToken ct)
    {
        if (fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit))
            return ServiceResult<AiConsentStatusDto>.Invalid("Fingerprint must be 64 hexadecimal characters.");

        var decision = await DecideAsync(ct);
        if (!decision.Available)
            return ServiceResult<AiConsentStatusDto>.Invalid("AI is not available on this installation.");

        var hasKey = await HasKeyAsync(userId, ct);
        var settings = await GetOrCreateSettingsAsync(userId, ct);
        if (settings is null) return ServiceResult<AiConsentStatusDto>.Unauthorized();
        if (!string.Equals(fingerprint, decision.Fingerprint, StringComparison.OrdinalIgnoreCase))
            // The provider changed after the user was shown the dialog: hand back the current
            // state so the client can show the new description instead of recording a consent
            // for something the user did not see.
            return ServiceResult<AiConsentStatusDto>.Conflict(ToStatus(decision, settings, hasKey));

        settings.AiConsentGrantedAt = DateTime.UtcNow;
        settings.AiConsentProviderFingerprint = decision.Fingerprint;
        await _db.SaveChangesAsync(ct);
        return ServiceResult<AiConsentStatusDto>.Success(ToStatus(decision, settings, hasKey));
    }

    public async Task<ServiceResult> WithdrawAsync(int userId, CancellationToken ct)
    {
        if (_apiKeys is null)
            throw new InvalidOperationException("Withdrawing consent needs the API key service to revoke the AI key.");

        var settings = await _db.Settings.FirstOrDefaultAsync(s => s.AuthUserId == userId, ct);
        if (settings is { AiConsentGrantedAt: not null } or { AiConsentProviderFingerprint: not null })
        {
            settings.AiConsentGrantedAt = null;
            settings.AiConsentProviderFingerprint = null;
            await _db.SaveChangesAsync(ct);
        }
        // The existing revoke path: removes the key hash and tells studylife-ai (through the
        // outbox, so an unreachable add-on is retried) to forget the key and purge the user's
        // indexed data.
        return await _apiKeys().RevokeAiKeyAsync(userId, ct);
    }

    public async Task<ServiceResult> EnsureGrantedAsync(int userId, CancellationToken ct)
    {
        // No add-on configured: nothing can leave this server, so there is nothing to ask. (The
        // proxy endpoints answer 503 for that case on their own.)
        if (!_client.Enabled) return ServiceResult.Success();

        var decision = await DecideAsync(ct);
        if (!decision.Required) return ServiceResult.Success();

        var settings = await ReadSettingsAsync(userId, ct);
        return Covers(settings, decision)
            ? ServiceResult.Success()
            : ServiceResult.Forbidden(ConsentRequiredProblem);
    }

    public async Task<bool> MayProcessInBackgroundAsync(int userId, CancellationToken ct)
    {
        // A demo instance never sends anything out (the write-block does not cover worker jobs).
        if (_demo || !await HasKeyAsync(userId, ct)) return false;
        return (await EnsureGrantedAsync(userId, ct)).Outcome == ServiceOutcome.Success;
    }

    public async Task<AiCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var decision = await DecideAsync(ct);
        return new AiCapabilities(decision.Available, ToDto(decision.Info));
    }

    // Every Settings access names the user explicitly, on top of the context's own per-user query
    // filter: a context scoped to a different user than the one asked about then finds no row, and
    // "no row" means "no consent" - the answer is never somebody else's consent.
    private Task<UserSettingsEntity?> ReadSettingsAsync(int userId, CancellationToken ct) =>
        _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.AuthUserId == userId, ct);

    // Null when the row that exists (or gets created) belongs to a different user than asked
    // about, i.e. the context is scoped to someone else: the caller fails closed instead of
    // writing a consent onto the wrong person.
    private async Task<UserSettingsEntity?> GetOrCreateSettingsAsync(int userId, CancellationToken ct)
    {
        var existing = await _db.Settings.FirstOrDefaultAsync(s => s.AuthUserId == userId, ct);
        if (existing is not null) return existing;
        var row = await _db.Settings.GetOrCreateAsync(_db);
        return row.AuthUserId == userId ? row : null;
    }

    private Task<bool> HasKeyAsync(int userId, CancellationToken ct) =>
        _db.AuthUsers.AsNoTracking().AnyAsync(u => u.Id == userId && u.AiApiKeyHash != null, ct);

    private static AiProviderInfoDto? ToDto(AiProviderInfo? info) => info is null
        ? null
        : new AiProviderInfoDto
        {
            AnyExternal = info.AnyExternal,
            Roles = info.Roles.Select(r => new AiProviderRoleDto
            {
                Role = r.Role,
                Provider = r.Provider,
                Model = r.Model,
                Local = r.Local,
            }).ToList(),
        };
}
