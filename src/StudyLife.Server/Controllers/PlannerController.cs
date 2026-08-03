using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Server-side variant of the exam planner from Client/Pages/Planner.razor - generates
/// and saves the suggested sessions directly, without a preview/confirm step. Exists so the
/// planning logic can also be triggered without a browser, e.g. from the Home Assistant
/// service "generate_exam_plan" (custom_components/studylife/services.py).
/// </summary>
[ApiController]
[Route("api/planner")]
public class PlannerController : ControllerBase
{
    private const int DefaultSessionLengthMinutes = 90;
    private const string ReviewTopicFallback = "Wiederholung";

    private readonly StudyLifeDb _db;

    public PlannerController(StudyLifeDb db) => _db = db;

    [HttpPost("exam-plan")]
    public async Task<ActionResult<List<StudySessionDto>>> GenerateExamPlan(ExamPlanRequestDto request)
    {
        if (request.ExamDate.Date <= DateTime.Today) return BadRequest("ExamDate must be in the future.");

        var settings = await _db.Settings.FirstOrDefaultAsync() ?? new UserSettingsEntity();

        // Course resolution is program-aware like ProgressController/CoursesController: an
        // active custom study program → its courses (tenant-separated), otherwise the built-in
        // catalog. Previously this endpoint (Home Assistant service "generate_exam_plan") only
        // knew CourseCatalog.AppliedAICourses.
        List<CourseDto> catalog;
        if (settings.ActiveStudyProgramId is int programId
            && await _db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == programId))
        {
            catalog = await Services.StudyProgramCatalog.LoadCoursesAsync(_db, programId);
        }
        else
        {
            catalog = CourseCatalog.AppliedAICourses;
        }
        var course = catalog.FirstOrDefault(c => c.Id == request.CourseId);
        if (course == null) return BadRequest($"Course ID {request.CourseId} is not in the active study program (/api/courses).");
        var goal = await _db.CourseGoals.FirstOrDefaultAsync(g => g.CourseId == request.CourseId);
        var completedTopics = string.IsNullOrWhiteSpace(goal?.CompletedTopics)
            ? new HashSet<string>()
            : goal!.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var openTopics = course.Topics.Where(t => !completedTopics.Contains(t)).ToList();

        var sessionLengthMinutes = request.SessionLengthMinutes is > 0 ? request.SessionLengthMinutes.Value : DefaultSessionLengthMinutes;
        var totalHours = request.TotalHours is > 0 ? request.TotalHours.Value : Math.Max(1.5, openTopics.Count * 1.5);
        var sessionLength = TimeSpan.FromMinutes(sessionLengthMinutes);
        var sessionsNeeded = Math.Max(1, (int)Math.Ceiling(totalHours * 60 / sessionLengthMinutes));

        var fromDate = DateTime.Now;
        var toDate = request.ExamDate.Date.AddDays(-1);
        var daysAvailable = Math.Max(1, (toDate.Date - fromDate.Date).Days + 1);
        var maxSlotsPerDay = Math.Clamp((int)Math.Ceiling(sessionsNeeded / (double)daysAvailable), 1, 3);

        var existing = await _db.Sessions
            .Where(s => s.StartTime >= fromDate.Date && s.StartTime <= toDate.Date.AddDays(1))
            .Select(s => new { s.StartTime, s.EndTime })
            .ToListAsync();
        var busy = existing.Select(s => (s.StartTime, s.EndTime));
        var allowedDays = StudyPlanner.ParseStudyDays(settings.StudyDays);

        var slots = StudyPlanner.FindFreeSlots(fromDate, toDate, busy, sessionLength, maxSlotsPerDay, sessionsNeeded,
            settings.StudyWindowStartHour, settings.StudyWindowEndHour, allowedDays);

        var created = new List<StudySessionEntity>();
        for (var i = 0; i < slots.Count; i++)
        {
            var entity = new StudySessionEntity
            {
                CourseId = course.Id,
                CourseName = course.Name,
                CourseColor = course.Color,
                StartTime = slots[i].Start,
                EndTime = slots[i].End,
                Topic = openTopics.Count > 0 ? openTopics[i % openTopics.Count] : ReviewTopicFallback,
                IsCompleted = false,
                TimerModeId = 1,
            };
            _db.Sessions.Add(entity);
            created.Add(entity);
        }
        await _db.SaveChangesAsync();

        return created.Select(e => new StudySessionDto
        {
            Id = e.Id,
            CourseId = e.CourseId,
            CourseName = e.CourseName,
            CourseColor = e.CourseColor,
            StartTime = e.StartTime,
            EndTime = e.EndTime,
            Topic = e.Topic,
            Notes = e.Notes,
            IsCompleted = e.IsCompleted,
            TimerModeId = e.TimerModeId,
            RecurrenceGroupId = e.RecurrenceGroupId,
        }).ToList();
    }
}
