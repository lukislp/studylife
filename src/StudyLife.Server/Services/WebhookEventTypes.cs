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
}
