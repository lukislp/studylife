using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// CRUD for session templates (feature "session templates for quickly creating recurring
/// sessions"). Deliberately no PUT/update for the MVP - delete + recreate is enough, see
/// the task description.
/// </summary>
[ApiController]
[Route("api/sessiontemplates")]
public class SessionTemplatesController : ControllerBase
{
    private readonly ISessionTemplateService _templates;

    public SessionTemplatesController(ISessionTemplateService templates) => _templates = templates;

    [HttpGet]
    public async Task<IEnumerable<SessionTemplateDto>> GetAll() => await _templates.GetAllAsync();

    [HttpPost]
    public async Task<ActionResult<SessionTemplateDto>> Create(SessionTemplateDto dto) =>
        (await _templates.CreateAsync(dto)).ToActionResult(this);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        (await _templates.DeleteAsync(id)).ToNoContentResult(this);
}
