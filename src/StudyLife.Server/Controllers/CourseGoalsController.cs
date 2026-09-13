using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/coursegoals")]
public class CourseGoalsController : ControllerBase
{
    private readonly ICourseGoalService _goals;

    public CourseGoalsController(ICourseGoalService goals) => _goals = goals;

    [HttpGet]
    public async Task<IEnumerable<CourseGoalDto>> GetAll() => await _goals.GetAllAsync();

    [HttpPut("{courseId}")]
    public async Task<ActionResult<CourseGoalDto>> Save(int courseId, CourseGoalDto dto) =>
        (await _goals.SaveAsync(courseId, dto)).ToActionResult(this);

    [HttpDelete("{courseId}")]
    public async Task<IActionResult> Delete(int courseId) =>
        (await _goals.DeleteAsync(courseId)).ToNoContentResult(this);
}
