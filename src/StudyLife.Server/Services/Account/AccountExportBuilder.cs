// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Reads the "account" block of the personal data export (GDPR Art. 15, 2026-11-17 audit F-9):
/// everything stored about a user besides the study content - sign-in methods, sessions,
/// devices, consents, keys, connected apps, invites, timer state. Every query names the user
/// explicitly, because most of these tables have no per-user query filter (sign-in looks them up
/// before a user is known). Each projection copies only descriptive fields: hashes, tokens, push
/// endpoints and keys never leave this class (pinned by PersonalDataExportTests).
/// </summary>
public interface IAccountExportBuilder
{
    Task<AccountExportDto> BuildAsync(int userId);
}

public sealed class AccountExportBuilder(StudyLifeDb db) : IAccountExportBuilder
{
    /// <summary>Name of the calendar feed link among <see cref="AccountExportDto.AccessKeys"/>.</summary>
    public const string CalendarPurpose = "calendar";

    public async Task<AccountExportDto> BuildAsync(int userId)
    {
        var user = await db.AuthUsers.AsNoTracking().SingleAsync(u => u.Id == userId);
        var settings = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.AuthUserId == userId);

        return new AccountExportDto
        {
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            IsOwner = user.IsOwner,
            RegisteredWithInviteAt = await db.AuthInvites.AsNoTracking()
                .Where(i => i.UsedByUserId == userId).Select(i => i.UsedAt).FirstOrDefaultAsync(),
            Password = await db.PasswordCredentials.AsNoTracking()
                .Where(p => p.AuthUserId == userId)
                .Select(p => new PasswordSignInExportDto
                {
                    Username = p.Username,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    LastUsedAt = p.LastUsedAt,
                    FailedAttemptCount = p.FailedAttemptCount,
                    LockedUntil = p.LockedUntil,
                }).FirstOrDefaultAsync(),
            Passkeys = await db.PasskeyCredentials.AsNoTracking()
                .Where(p => p.AuthUserId == userId).OrderBy(p => p.CreatedAt)
                .Select(p => new PasskeyExportDto
                {
                    Label = p.DeviceLabel,
                    CreatedAt = p.CreatedAt,
                    LastUsedAt = p.LastUsedAt,
                    ApprovedAt = p.ApprovedAt,
                }).ToListAsync(),
            SignInSessions = await db.AuthSessions.AsNoTracking()
                .Where(s => s.AuthUserId == userId).OrderBy(s => s.IssuedAt)
                .Select(s => new SignInSessionExportDto
                {
                    IssuedAt = s.IssuedAt,
                    LastUsedAt = s.LastUsedAt,
                    ExpiresAt = s.ExpiresAt,
                    HardExpiresAt = s.HardExpiresAt,
                }).ToListAsync(),
            RecoveryCodes = new RecoveryCodesExportDto
            {
                Remaining = await db.RecoveryCodes.CountAsync(c => c.AuthUserId == userId && c.UsedAt == null),
            },
            PushDevices = await db.PushSubscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.AuthUserId == userId).OrderBy(p => p.Id)
                .Select(p => new PushDeviceExportDto { Channel = p.Channel, Device = p.UserAgent, CreatedAt = p.CreatedAt })
                .ToListAsync(),
            AiConsent = new AiConsentExportDto
            {
                GrantedAt = settings?.AiConsentGrantedAt,
                ProviderFingerprint = settings?.AiConsentProviderFingerprint,
            },
            TelemetryConsent = new TelemetryConsentExportDto
            {
                Value = settings?.TelemetryConsent,
                AnsweredAt = settings?.TelemetryConsentAt,
                TextVersion = settings?.TelemetryConsentVersion,
            },
            AccessKeys = AccessKeys(user),
            WebhookKeys = await db.WebhookApiKeys.AsNoTracking()
                .Where(k => k.AuthUserId == userId).OrderBy(k => k.CreatedAt)
                .Select(k => new WebhookKeyExportDto { Name = k.Name, CreatedAt = k.CreatedAt })
                .ToListAsync(),
            ConnectedApps = await ConnectedAppsAsync(userId),
            RegisteredApps = await RegisteredAppsAsync(userId),
            InvitesCreated = await db.AuthInvites.AsNoTracking()
                .Where(i => i.CreatedByUserId == userId).OrderBy(i => i.CreatedAt)
                .Select(i => new InviteExportDto { CreatedAt = i.CreatedAt, ExpiresAt = i.ExpiresAt, UsedAt = i.UsedAt })
                .ToListAsync(),
            TimerState = await db.TimerState.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.AuthUserId == userId)
                .Select(t => new TimerStateExportDto
                {
                    SessionId = t.SessionId,
                    IsRunning = t.IsRunning,
                    IsBreak = t.IsBreak,
                    CurrentRound = t.CurrentRound,
                    TimerModeId = t.TimerModeId,
                    PhaseEndsAt = t.PhaseEndsAt,
                    UpdatedAt = t.UpdatedAt,
                }).FirstOrDefaultAsync(),
            SentNotifications = await db.SentReminders.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.AuthUserId == userId).OrderBy(r => r.SentAt)
                .Select(r => new SentNotificationExportDto { Key = r.Key, SentAt = r.SentAt })
                .ToListAsync(),
        };
    }

    /// <summary>Which access keys exist and since when - decided by the hash being present, the
    /// hash itself is never copied.</summary>
    private static List<AccessKeyExportDto> AccessKeys(AuthUserEntity user)
    {
        var slots = new (string Purpose, string? Hash, DateTime? CreatedAt)[]
        {
            ("ha", user.ApiKeyHash, user.ApiKeyCreatedAt),
            ("ai", user.AiApiKeyHash, user.AiApiKeyCreatedAt),
            ("mcp", user.McpApiKeyHash, user.McpApiKeyCreatedAt),
            ("capture", user.CaptureApiKeyHash, user.CaptureApiKeyCreatedAt),
            ("focusguard", user.FocusGuardApiKeyHash, user.FocusGuardApiKeyCreatedAt),
            ("focustunes", user.FocusTunesApiKeyHash, user.FocusTunesApiKeyCreatedAt),
            ("tray", user.TrayApiKeyHash, user.TrayApiKeyCreatedAt),
            ("developer", user.DeveloperApiKeyHash, user.DeveloperApiKeyCreatedAt),
            (CalendarPurpose, user.CalendarTokenHash, user.CalendarTokenCreatedAt),
        };
        return slots
            .Where(s => s.Hash is not null)
            .Select(s => new AccessKeyExportDto { Purpose = s.Purpose, CreatedAt = s.CreatedAt })
            .ToList();
    }

    private async Task<List<ConnectedAppExportDto>> ConnectedAppsAsync(int userId)
    {
        var grants = await db.ClientApiKeys.AsNoTracking()
            .Where(k => k.AuthUserId == userId).OrderBy(k => k.CreatedAt)
            .Select(k => new { k.ClientId, k.GrantedScopes, k.CreatedAt })
            .ToListAsync();
        var clientIds = grants.Select(g => g.ClientId).Distinct().ToList();
        var names = await db.OAuthClients.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId))
            .ToDictionaryAsync(c => c.ClientId, c => c.Name);

        return grants.Select(g => new ConnectedAppExportDto
        {
            ClientId = g.ClientId,
            Name = names.GetValueOrDefault(g.ClientId),
            GrantedScopes = ScopeNames(g.GrantedScopes),
            CreatedAt = g.CreatedAt,
        }).ToList();
    }

    private async Task<List<RegisteredAppExportDto>> RegisteredAppsAsync(int userId)
    {
        var apps = await db.OAuthClients.AsNoTracking()
            .Where(c => c.OwnerAuthUserId == userId).OrderBy(c => c.CreatedAt)
            .ToListAsync();
        return apps.Select(c => new RegisteredAppExportDto
        {
            ClientId = c.ClientId,
            Name = c.Name,
            Description = c.Description,
            RedirectUris = c.AllowedRedirectUris.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            RequestedScopes = ScopeNames(c.RequestedScopes),
            Approval = OAuthClientApprovalPolicy.ToWire(c.Approval),
            CreatedAt = c.CreatedAt,
        }).ToList();
    }

    // Same "Controller.Action" form DeveloperClientService shows the developer.
    private static List<string> ScopeNames(string stored) =>
        ApiKeyScopes.Parse(stored).Select(e => $"{e.Controller}.{e.Action}").Order(StringComparer.Ordinal).ToList();
}
