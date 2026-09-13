using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Custom study programs: list for the switcher in setup, detail (group quotas) for the
/// client's program-aware ECTS calculation, and creating a complete study program in one
/// call. The fixed built-in study program has no DB row and only appears as a synthetic
/// list entry (Id == null). Switching the active study program does NOT go through here,
/// but as a normal settings field (UserSettings.ActiveStudyProgramId, PUT /api/settings) -
/// consistent with all other settings.
/// </summary>
[ApiController]
[Route("api/studyprograms")]
public class StudyProgramsController : ControllerBase
{
    private readonly IStudyProgramService _programs;

    public StudyProgramsController(IStudyProgramService programs) => _programs = programs;

    [HttpGet]
    public async Task<ActionResult<List<StudyProgramSummaryDto>>> GetAll() => await _programs.GetSummariesAsync();

    /// <summary>
    /// Sets/removes the purely MANUAL completion flag of a custom study program. No
    /// automation: the flag is only ever changed through here, never, e.g., at 100% ECTS.
    /// The built-in study program has no DB row and therefore cannot be marked (404).
    /// </summary>
    [HttpPut("{id:int}/completed")]
    public async Task<ActionResult<StudyProgramSummaryDto>> SetCompleted(int id, SetStudyProgramCompletedDto request) =>
        (await _programs.SetCompletedAsync(id, request)).ToActionResult(this);

    /// <summary>
    /// Deletes a custom study program along with its elective groups and courses - see
    /// StudyProgramService.DeleteAsync for what is deliberately NOT deleted along with it and
    /// why deleting a user's last remaining program can be refused with 400.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) =>
        (await _programs.DeleteAsync(id)).ToNoContentResult(this);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StudyProgramDetailDto>> Get(int id) =>
        (await _programs.GetAsync(id)).ToActionResult(this);

    [HttpPost]
    public async Task<ActionResult<StudyProgramSummaryDto>> Create(CreateStudyProgramRequestDto request) =>
        (await _programs.CreateAsync(request)).ToActionResult(this);
}
