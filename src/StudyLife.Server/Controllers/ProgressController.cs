using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Read-only progress link for sharing with third parties (parents/mentor/study advisor),
/// without login/API key. Deliberately its OWN token per settings row instead of the
/// calendar token: semantically separate (the calendar shows appointments, this link shows a
/// progress snapshot) and independently regenerable. The token lives in
/// UserSettingsEntity.ProgressShareToken, because it needs to be toggleable together with an
/// Enabled flag per user/settings row (setup UI: toggle instead of pure rotation).
/// GET /api/progress/shared/{token} is exempt from the normal API-key check in Program.cs
/// (like /api/sessions/ics) - the token check happens here in the controller itself, because
/// it needs DB access.
/// </summary>
[ApiController]
[Route("api/progress")]
public class ProgressController : ControllerBase
{
    private readonly StudyLifeDb _db;

    public ProgressController(StudyLifeDb db) => _db = db;

    /// <summary>
    /// Compact progress snapshot: total/earned ECTS, ECTS-weighted grade average, active
    /// courses with topic progress. NO access to notes, session details, or other settings -
    /// deliberately only what CourseCatalog/StudyMetrics already compute for ECTS/grade
    /// progress anyway (see CoursesController/CourseGoalsController). Invalid/missing token OR
    /// a disabled feature (even with a valid token present) → 404, deliberately not 401/403:
    /// a 404 doesn't even reveal to a scanner whether the path exists. DELIBERATELY searches
    /// for the token via IgnoreQueryFilters across ALL users, not just the one ambiently
    /// resolved via the gate (fallback to the first AuthUser) - otherwise every user's link
    /// except the very first would 404 for no reason (security fix: previously the check ran
    /// against the wrong, ambiently resolved settings row). The found token uniquely
    /// identifies its owner (32-byte CSPRNG, practically collision-free) - that's the actual
    /// authorization here, not a substitute for it.
    /// PublicUnlessInvalidSession (not plain [AllowAnonymous]): reachable without any
    /// credential, but an X-Session-Token that IS present and invalid is still rejected -
    /// matches the former resolution middleware's behavior for this exempt path.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.PublicUnlessInvalidSession)]
    [HttpGet("shared/{token}")]
    public async Task<ActionResult<ProgressShareDto>> GetShared(string token)
    {
        var settings = await _db.Settings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProgressShareEnabled && s.ProgressShareToken == token);
        if (settings == null)
            return NotFound();

        // The remaining queries of this method (CourseGoals, StudyProgramCatalog) still run
        // through the normal query filters - they must therefore point to the actual token
        // owner, not to the (fallback) user ambiently resolved by the gate.
        using var _ = CurrentUserAccessor.BeginBackgroundScope(settings.AuthUserId);

        var programId = settings.ActiveStudyProgramId;
        List<CourseDto> courseList;
        IReadOnlyDictionary<string, int> groupQuotas;
        if (programId.HasValue)
        {
            courseList = await StudyProgramCatalog.LoadCoursesAsync(_db, programId.Value);
            groupQuotas = await StudyProgramCatalog.LoadGroupQuotasAsync(_db, programId.Value);
        }
        else
        {
            courseList = CourseCatalog.AppliedAICourses;
            groupQuotas = CourseCatalog.GroupEctsQuotas;
        }

        var selectedIds = CommaSeparatedIds.Parse(settings.SelectedCourseIds);
        var completedIds = CommaSeparatedIds.Parse(settings.CompletedCourseIds);

        var totalEcts = CourseCatalog.CalcTotalEcts(courseList, groupQuotas);
        var earnedEcts = CourseCatalog.CalcEctsEarned(courseList, completedIds, groupQuotas);

        var goals = await _db.CourseGoals.AsNoTracking().ToListAsync();
        var goalsByCourseId = goals.ToDictionary(g => g.CourseId);

        var gradedCourses = new List<StudyMetrics.GradedCourse>();
        foreach (var c in courseList)
        {
            if (!completedIds.Contains(c.Id)) continue;
            if (goalsByCourseId.TryGetValue(c.Id, out var g) && g.Grade.HasValue)
                gradedCourses.Add(new StudyMetrics.GradedCourse(g.Grade.Value, c.Ects));
        }
        var averageGrade = StudyMetrics.CalcWeightedAverageGrade(gradedCourses);

        var activeIds = new HashSet<int>(selectedIds);
        activeIds.ExceptWith(completedIds);
        var activeCourses = courseList
            .Where(c => activeIds.Contains(c.Id))
            .OrderBy(c => c.Semester).ThenBy(c => c.Name)
            .Select(c => new ProgressShareCourseDto
            {
                Name = c.Name,
                Icon = c.Icon,
                Color = c.Color,
                Ects = c.Ects,
                Semester = c.Semester,
                TopicProgressPercent = ComputeTopicProgressPercent(c, goalsByCourseId.GetValueOrDefault(c.Id)),
            })
            .ToList();

        return new ProgressShareDto
        {
            TotalEcts = totalEcts,
            EarnedEcts = earnedEcts,
            AverageGrade = averageGrade,
            CoursesCompletedCount = completedIds.Count,
            CoursesTotalCount = courseList.Count,
            ActiveCourses = activeCourses,
            GeneratedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Share of checked-off topics (CourseGoalEntity.CompletedTopics) out of CourseDto.Topics, 0-100. No topics recorded → 0.</summary>
    private static int ComputeTopicProgressPercent(CourseDto course, CourseGoalEntity? goal)
    {
        if (course.Topics.Count == 0) return 0;
        var completedTopics = string.IsNullOrWhiteSpace(goal?.CompletedTopics)
            ? new HashSet<string>()
            : goal!.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var doneCount = course.Topics.Count(t => completedTopics.Contains(t));
        return (int)Math.Round(doneCount * 100.0 / course.Topics.Count);
    }
}
