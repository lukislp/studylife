using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
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
    private const int MinSessionLengthMinutes = 5;
    private const int MaxSessionLengthMinutes = 480;
    private const double MaxTotalHours = 1000;
    private const int MaxYearsAhead = 2;
    private const string ReviewTopicFallback = "Wiederholung";

    private readonly StudyLifeDb _db;
    private readonly ICourseResolver _courseResolver;
    private readonly WebhooksProxyClient _webhooks;
    private readonly ICurrentUserAccessor _currentUser;

    public PlannerController(StudyLifeDb db, ICourseResolver courseResolver,
        WebhooksProxyClient webhooks, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _courseResolver = courseResolver;
        _webhooks = webhooks;
        _currentUser = currentUser;
    }

    [HttpPost("exam-plan")]
    public async Task<ActionResult<List<StudySessionDto>>> GenerateExamPlan(ExamPlanRequestDto request)
    {
        if (request.ExamDate.Date <= DateTime.Today) return BadRequest("ExamDate must be in the future.");
        // Upper bounds (2026-09 audit S2): without them "examDate=2999, totalHours=30000,
        // sessionLengthMinutes=1" materialized millions of StudySessionEntity objects into one
        // SaveChanges - an OOM on the target hardware plus a bloated database, reachable with a
        // plain HA API key. Two years at 3 slots/day caps a single plan at ~2200 sessions.
        if (request.ExamDate.Date > DateTime.Today.AddYears(MaxYearsAhead))
            return BadRequest($"ExamDate must be within the next {MaxYearsAhead} years.");
        if (request.SessionLengthMinutes is > 0 and (< MinSessionLengthMinutes or > MaxSessionLengthMinutes))
            return BadRequest($"SessionLengthMinutes must be between {MinSessionLengthMinutes} and {MaxSessionLengthMinutes}.");
        if (request.TotalHours is > MaxTotalHours)
            return BadRequest($"TotalHours must be at most {MaxTotalHours}.");

        var settings = await _db.Settings.FirstOrDefaultAsync() ?? new UserSettingsEntity();

        var course = await _courseResolver.ResolveInActiveProgramAsync(request.CourseId, settings);
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

        _ = _webhooks.PublishEventAsync(_currentUser.AuthUserId, WebhookEventTypes.PlanGenerated,
            new { courseId = course.Id, courseName = course.Name, sessionsCreated = created.Count, examDate = request.ExamDate },
            CancellationToken.None);

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
