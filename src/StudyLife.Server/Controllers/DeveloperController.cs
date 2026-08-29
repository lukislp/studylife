using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// CRUD for OAuthClientEntity - lets any logged-in user register/manage their own add-ons
/// against the generic connect flow (AuthController.10.OAuthClients.cs). This is the only
/// backend dependency the studylife-developers portal has: it authenticates with its own
/// dedicated toggle-style key (AuthUserEntity.DeveloperApiKeyHash, see SettingsController's
/// developer-api-key group and ApiKeyScopes.Developer) - deliberately NOT the generic
/// add-on-connect flow (AuthController.10.OAuthClients.cs) that flow is for INSTALLED
/// third-party add-ons requesting DATA access; granting that same mechanism the ability to
/// manage OTHER clients' registrations would let any installed add-on mint arbitrarily-scoped
/// new clients for other users to unwittingly consent to. No explicit [Authorize] here at all -
/// falls through to the default ApiAccess fallback policy (session OR any scoped API key +
/// ApiKeyScopeAuthorizationHandler's per-slot enforcement), exactly like WebhooksProxyController.
///
/// Every action filters explicitly by OwnerAuthUserId - OAuthClientEntity carries no query
/// filter at the EF level (needed for the connect flow's own by-ClientId lookup before any user
/// is known, see StudyLifeDb's comment on it), so ownership is enforced here instead, same
/// "user-specific accesses filter explicitly in the controller" pattern as WebhookApiKeyEntity/
/// SettingsController.
/// </summary>
[ApiController]
[Route("api/developer/clients")]
public class DeveloperController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly ICurrentUserAccessor _currentUser;

    public DeveloperController(StudyLifeDb db, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<List<DeveloperClientDto>>> GetAll()
    {
        var userId = _currentUser.AuthUserId;
        return await _db.OAuthClients.AsNoTracking()
            .Where(c => c.OwnerAuthUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => ToDto(c))
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<DeveloperClientDto>> Create(CreateDeveloperClientRequestDto request)
    {
        var error = Validate(request.ClientId, request.Name, request.AllowedRedirectUris, request.RequestedScopes);
        if (error != null) return BadRequest(error);

        if (await _db.OAuthClients.AnyAsync(c => c.ClientId == request.ClientId))
            return BadRequest($"ClientId '{request.ClientId}' is already taken.");

        var userId = _currentUser.AuthUserId;
        var entity = new OAuthClientEntity
        {
            ClientId = request.ClientId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? "",
            AllowedRedirectUris = string.Join(',', request.AllowedRedirectUris),
            RequestedScopes = SerializeScopeStrings(request.RequestedScopes),
            OwnerAuthUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.OAuthClients.Add(entity);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    /// <summary>Name/Description/AllowedRedirectUris/RequestedScopes are all editable after the
    /// fact - adding a scope here never widens access already granted to an existing installer,
    /// see ClientApiKeyEntity.GrantedScopes for why. ClientId itself never changes once
    /// registered (it's the public identifier third parties and the marketplace manifest key
    /// off).</summary>
    [HttpPut("{clientId}")]
    public async Task<ActionResult<DeveloperClientDto>> Update(string clientId, UpdateDeveloperClientRequestDto request)
    {
        var userId = _currentUser.AuthUserId;
        var entity = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId && c.OwnerAuthUserId == userId);
        if (entity is null) return NotFound();

        var error = Validate(clientId, request.Name, request.AllowedRedirectUris, request.RequestedScopes);
        if (error != null) return BadRequest(error);

        entity.Name = request.Name.Trim();
        entity.Description = request.Description?.Trim() ?? "";
        entity.AllowedRedirectUris = string.Join(',', request.AllowedRedirectUris);
        entity.RequestedScopes = SerializeScopeStrings(request.RequestedScopes);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    [HttpDelete("{clientId}")]
    public async Task<IActionResult> Delete(string clientId)
    {
        var userId = _currentUser.AuthUserId;
        var entity = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId && c.OwnerAuthUserId == userId);
        if (entity is null) return NotFound();
        _db.OAuthClients.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string SerializeScopeStrings(List<string> scopes) =>
        ApiKeyScopes.Serialize(scopes
            .Select(s => s.Split('.', 2))
            .Where(p => p.Length == 2)
            .Select(p => new ApiKeyScopes.Endpoint(p[0], p[1])));

    private static string? Validate(string clientId, string name, List<string> redirectUris, List<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100
            || !clientId.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return "ClientId must be a lowercase alphanumeric-and-hyphen slug, at most 100 characters.";
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return "Name must not be empty and at most 100 characters long.";
        if (redirectUris.Count == 0)
            return "At least one redirect URI is required.";
        foreach (var uri in redirectUris)
        {
            if (!AuthController.IsAllowedRedirectUri(uri))
                return $"Redirect URI '{uri}' must be an absolute https URL, or an http://127.0.0.1|localhost loopback URL (RFC 8252).";
        }
        if (scopes.Count == 0)
            return "At least one scope is required.";
        foreach (var scope in scopes)
        {
            var parts = scope.Split('.', 2);
            if (parts.Length != 2 || !ApiKeyScopes.PubliclyGrantable.Contains(new ApiKeyScopes.Endpoint(parts[0], parts[1])))
                return $"Scope '{scope}' is not a publicly grantable scope.";
        }
        return null;
    }

    private static DeveloperClientDto ToDto(OAuthClientEntity e) => new()
    {
        ClientId = e.ClientId,
        Name = e.Name,
        Description = e.Description,
        AllowedRedirectUris = e.AllowedRedirectUris.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        RequestedScopes = ApiKeyScopes.Parse(e.RequestedScopes).Select(x => $"{x.Controller}.{x.Action}").ToList(),
        CreatedAt = e.CreatedAt,
    };
}
