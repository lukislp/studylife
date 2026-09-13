using Microsoft.AspNetCore.Mvc;
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
/// Ownership filtering lives in DeveloperClientService (OAuthClientEntity carries no EF query
/// filter, see StudyLifeDb's comment on it).
/// </summary>
[ApiController]
[Route("api/developer/clients")]
public class DeveloperController : ControllerBase
{
    private readonly IDeveloperClientService _clients;

    public DeveloperController(IDeveloperClientService clients) => _clients = clients;

    [HttpGet]
    public async Task<ActionResult<List<DeveloperClientDto>>> GetAll() => await _clients.GetAllAsync();

    [HttpPost]
    public async Task<ActionResult<DeveloperClientDto>> Create(CreateDeveloperClientRequestDto request) =>
        (await _clients.CreateAsync(request)).ToActionResult(this);

    /// <summary>Name/Description/AllowedRedirectUris/RequestedScopes are all editable after the
    /// fact - adding a scope here never widens access already granted to an existing installer,
    /// see ClientApiKeyEntity.GrantedScopes for why. ClientId itself never changes once
    /// registered (it's the public identifier third parties and the marketplace manifest key
    /// off).</summary>
    [HttpPut("{clientId}")]
    public async Task<ActionResult<DeveloperClientDto>> Update(string clientId, UpdateDeveloperClientRequestDto request) =>
        (await _clients.UpdateAsync(clientId, request)).ToActionResult(this);

    [HttpDelete("{clientId}")]
    public async Task<IActionResult> Delete(string clientId) =>
        (await _clients.DeleteAsync(clientId)).ToNoContentResult(this);
}
