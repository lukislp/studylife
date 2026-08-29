namespace StudyLife.Server.Services;

/// <summary>
/// Catalog of every event type this server currently publishes to studylife-webhooks (see
/// WebhooksProxyClient.PublishEventAsync). Deliberately a flat list of string constants, not a
/// closed enum shared with the microservice: studylife-webhooks never validates an event string
/// against this list, it only matches a user's own subscriptions against whatever string arrives
/// - so a brand-new event type only ever needs a new call site here plus a new constant, never a
/// change on the receiving end. This list exists purely so call sites here don't hand-type the
/// same string twice.
/// </summary>
public static class WebhookEventTypes
{
    /// <summary>Server-observed transition false-&gt;true on TimerStateController.Save. Cannot
    /// distinguish "Start" from "Reset while already stopped" the way the client's own
    /// TimerService can, because Reset() only reaches the server at all when a session was
    /// actually running (see studylife's TimerService.Reset - the no-op case never PUTs).</summary>
    public const string TimerStarted = "timer.started";

    /// <summary>Server-observed transition true-&gt;false on TimerStateController.Save - covers
    /// Pause, Reset-while-running, and a session simply completing; the server has no way to
    /// distinguish which of these caused it, only that a session that was running no longer is.</summary>
    public const string TimerEnded = "timer.ended";

    public const string SessionCreated = "session.created";
    public const string SessionCompleted = "session.completed";
    public const string SessionDeleted = "session.deleted";

    /// <summary>Fired at the same point SessionsController.CheckNewRecordAsync already sends its
    /// push notification - "longest single session so far" (see that method's doc comment).</summary>
    public const string NewRecordSet = "new_record.set";

    public const string NoteCreated = "note.created";
    public const string NoteUpdated = "note.updated";
    public const string NoteDeleted = "note.deleted";

    /// <summary>CourseGoalsController.Save is an upsert (PUT) - Created/Updated distinguish
    /// which branch ran, Completed fires additionally (not instead) the moment CompletedAt
    /// transitions from unset to set, same "transition, not level" reasoning as
    /// SessionCompleted/TimerStarted/TimerEnded above.</summary>
    public const string CourseGoalCreated = "course_goal.created";
    public const string CourseGoalUpdated = "course_goal.updated";
    public const string CourseGoalCompleted = "course_goal.completed";
    public const string CourseGoalDeleted = "course_goal.deleted";

    public const string CourseResourceCreated = "course_resource.created";
    public const string CourseResourceDeleted = "course_resource.deleted";

    public const string SessionTemplateCreated = "session_template.created";
    public const string SessionTemplateDeleted = "session_template.deleted";

    public const string StudyProgramCreated = "study_program.created";

    /// <summary>StudyProgramsController.SetCompleted toggles a purely manual flag both ways -
    /// only fired on the false-&gt;true transition, mirroring SessionCompleted (an un-complete
    /// is not itself considered a noteworthy event to subscribe to).</summary>
    public const string StudyProgramCompleted = "study_program.completed";
    public const string StudyProgramDeleted = "study_program.deleted";

    /// <summary>PlannerController.GenerateExamPlan bulk-inserts StudySessionEntity rows
    /// directly (not through SessionsController), so none of those individually fire
    /// SessionCreated - this single summary event is the only webhook signal for the whole
    /// batch.</summary>
    public const string PlanGenerated = "plan.generated";
}
