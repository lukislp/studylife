using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Server-side variant of the exam planner from Client/Pages/Planner.razor - generates
/// and saves the suggested sessions directly, without a preview/confirm step. Exists so the
/// planning logic can also be triggered without a browser, e.g. from the Home Assistant
/// service "generate_exam_plan" (custom_components/studylife/services.py). The planning itself
/// lives in ExamPlanService.
/// </summary>
[ApiController]
[Route("api/planner")]
public class PlannerController : ControllerBase
{
    private readonly IExamPlanService _examPlans;

    public PlannerController(IExamPlanService examPlans) => _examPlans = examPlans;

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitPolicies.Expensive)]
    [HttpPost("exam-plan")]
    public async Task<ActionResult<List<StudySessionDto>>> GenerateExamPlan(ExamPlanRequestDto request) =>
        (await _examPlans.GenerateAsync(request)).ToActionResult(this);
}
