using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// CRUD for the course resource collection (setup page, CourseResourcesModal.razor).
/// Deliberately no PUT/update - delete + recreate is enough for the manageable number of
/// entries per course.
/// </summary>
[ApiController]
[Route("api/courseresources")]
public class CourseResourcesController : ControllerBase
{
    private readonly ICourseResourceService _resources;

    public CourseResourcesController(ICourseResourceService resources) => _resources = resources;

    [HttpGet]
    public async Task<IEnumerable<CourseResourceDto>> GetByCourse([FromQuery] int courseId) =>
        await _resources.GetByCourseAsync(courseId);

    [HttpPost]
    public async Task<ActionResult<CourseResourceDto>> Create(CourseResourceDto dto) =>
        (await _resources.CreateAsync(dto)).ToActionResult(this);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        (await _resources.DeleteAsync(id)).ToNoContentResult(this);
}
