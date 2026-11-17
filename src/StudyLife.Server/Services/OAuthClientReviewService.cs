// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Everything the instance owner and the consent screen need to know about who stands behind an
/// OAuth client registration (2026-11-16 audit M5). Read side: <see cref="GetConsentInfoAsync"/>
/// feeds the consent page (name, scopes, registrant, review state, redirect URIs). Owner side:
/// list, approve and reject registrations made by other users - see
/// <see cref="OAuthClientApprovalPolicy"/> for when a registration needs review at all.
/// OAuthClientReviewController and AuthController.GetOAuthClientInfo only map these results to
/// HTTP.
/// </summary>
public interface IOAuthClientReviewService
{
    /// <summary>Null when no client with that id is registered.</summary>
    Task<OAuthClientInfoDto?> GetConsentInfoAsync(string clientId);
    Task<ServiceResult<List<OAuthClientReviewDto>>> ListAsync();
    Task<ServiceResult<OAuthClientReviewDto>> ApproveAsync(string clientId);
    Task<ServiceResult<OAuthClientReviewDto>> RejectAsync(string clientId);
}

public class OAuthClientReviewService(StudyLifeDb db, ICurrentUserAccessor currentUser, IOwnershipService ownership) : IOAuthClientReviewService
{
    /// <summary>Stable 403 problem code for every owner-only action here.</summary>
    public const string OwnerRequiredProblem = "owner-required";

    public async Task<OAuthClientInfoDto?> GetConsentInfoAsync(string clientId)
    {
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null) return null;
        var registrant = await db.AuthUsers.AsNoTracking()
            .Where(u => u.Id == client.OwnerAuthUserId)
            .Select(u => new { u.DisplayName, u.IsOwner })
            .FirstOrDefaultAsync();
        return new OAuthClientInfoDto
        {
            Name = client.Name,
            Description = client.Description,
            RequestedScopes = ScopeStrings(client.RequestedScopes),
            RegisteredBy = registrant?.DisplayName,
            RegisteredByOwner = registrant?.IsOwner ?? false,
            Approval = OAuthClientApprovalPolicy.ToWire(client.Approval),
            RedirectUris = RedirectUriList(client.AllowedRedirectUris),
        };
    }

    /// <summary>Every registration made by someone other than the owner, pending ones first so the
    /// open decisions lead the list. The owner's own registrations never need review and are left
    /// out.</summary>
    public async Task<ServiceResult<List<OAuthClientReviewDto>>> ListAsync()
    {
        if (!await CallerIsOwnerAsync()) return ServiceResult<List<OAuthClientReviewDto>>.Forbidden(OwnerRequiredProblem);

        var ownerIds = await db.AuthUsers.AsNoTracking().Where(u => u.IsOwner).Select(u => u.Id).ToListAsync();
        var clients = await db.OAuthClients.AsNoTracking()
            .Where(c => !ownerIds.Contains(c.OwnerAuthUserId))
            .ToListAsync();
        var registrantIds = clients.Select(c => c.OwnerAuthUserId).Distinct().ToList();
        var names = await db.AuthUsers.AsNoTracking()
            .Where(u => registrantIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return ServiceResult<List<OAuthClientReviewDto>>.Success(clients
            .OrderBy(c => c.Approval == OAuthClientApproval.Pending ? 0 : 1)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => ToReviewDto(c, names.GetValueOrDefault(c.OwnerAuthUserId)))
            .ToList());
    }

    public Task<ServiceResult<OAuthClientReviewDto>> ApproveAsync(string clientId) =>
        DecideAsync(clientId, OAuthClientApproval.Approved);

    /// <summary>Rejecting also revokes every key already issued for the client: a registration the
    /// owner no longer trusts must not keep working through keys handed out while it was approved
    /// (same reasoning as DeveloperClientService.DeleteAsync).</summary>
    public Task<ServiceResult<OAuthClientReviewDto>> RejectAsync(string clientId) =>
        DecideAsync(clientId, OAuthClientApproval.Rejected);

    private async Task<ServiceResult<OAuthClientReviewDto>> DecideAsync(string clientId, OAuthClientApproval decision)
    {
        if (!await CallerIsOwnerAsync()) return ServiceResult<OAuthClientReviewDto>.Forbidden(OwnerRequiredProblem);

        var client = await db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null) return ServiceResult<OAuthClientReviewDto>.NotFound();

        client.Approval = decision;
        if (decision == OAuthClientApproval.Rejected)
            await db.ClientApiKeys.Where(k => k.ClientId == clientId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        var registeredBy = await db.AuthUsers.AsNoTracking()
            .Where(u => u.Id == client.OwnerAuthUserId).Select(u => u.DisplayName).FirstOrDefaultAsync();
        return ServiceResult<OAuthClientReviewDto>.Success(ToReviewDto(client, registeredBy));
    }

    private Task<bool> CallerIsOwnerAsync() => ownership.IsOwnerAsync(currentUser.AuthUserId);

    private static OAuthClientReviewDto ToReviewDto(OAuthClientEntity c, string? registeredBy) => new()
    {
        ClientId = c.ClientId,
        Name = c.Name,
        Description = c.Description,
        RedirectUris = RedirectUriList(c.AllowedRedirectUris),
        RequestedScopes = ScopeStrings(c.RequestedScopes),
        RegisteredBy = registeredBy,
        Approval = OAuthClientApprovalPolicy.ToWire(c.Approval),
        CreatedAt = c.CreatedAt,
    };

    private static List<string> ScopeStrings(string serialized) =>
        ApiKeyScopes.Parse(serialized).Select(e => $"{e.Controller}.{e.Action}").ToList();

    private static List<string> RedirectUriList(string serialized) =>
        serialized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
