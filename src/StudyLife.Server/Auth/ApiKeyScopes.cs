namespace StudyLife.Server.Auth;

/// <summary>
/// ONE explicit, auditable map of "which controller actions may a given API-key SLOT reach" -
/// audit finding A6 round 2: the four per-user key slots (ha/ai/mcp/capture on AuthUserEntity)
/// used to be pure rotation boundaries, every one of them granting the FULL /api surface once
/// authenticated (see StudyLifeAuthenticationHandler). A leaked browser-extension (capture) key
/// could therefore read every note/session and delete data - this map is what
/// ApiKeyScopeAuthorizationHandler enforces per request to close that gap.
///
/// Matched on (ControllerName, ActionName) from ASP.NET Core's own ControllerActionDescriptor -
/// deliberately NOT the raw route template string: controller/action names are exactly what the
/// endpoint's C# source already declares (no risk of a route-template typo silently widening or
/// narrowing a grant), and they read directly against the consumer citations below.
///
/// Every entry cites the ACTUAL external consumer that needs it (inventoried from each sibling
/// repo's real client code, not from what an integration theoretically could want) - a slot with
/// no plausible real-world use for an endpoint simply has no entry, which denies it by default
/// (safer than an allow-list that has to be kept in sync by omission). Adding a new capability to
/// any integration must add its endpoint here explicitly, in the same change.
///
/// Session credentials and the ICS calendar token are NOT looked up here at all - both keep full
/// access unconditionally (see ApiKeyScopeAuthorizationHandler), same as before this change:
/// - Session: the browser client already reaches everything a logged-in user can do; scoping it
///   would break the app itself, not just narrow an integration.
/// - Calendar token: by construction (StudyLifeAuthenticationHandler) it can NEVER authenticate
///   any request other than GET /api/sessions/ics - there is nothing left to scope.
/// </summary>
public static class ApiKeyScopes
{
    /// <summary>One (controller, action) pair, exactly as ControllerActionDescriptor names them
    /// (i.e. the class name without the "Controller" suffix, and the method name).</summary>
    public readonly record struct Endpoint(string Controller, string Action);

    /// <summary>Identity contract v1 §1: every slot must be able to resolve which user/credential
    /// it is (AuthController.Whoami, GET /api/auth/whoami) - needed by all three satellite
    /// repos (studylife-hacs, studylife-mcp, studylife-ai) and the capture extension alike for
    /// diagnosing a misconfigured/rejected key, so it is added to every slot below instead of
    /// being special-cased outside the map.</summary>
    private static readonly Endpoint Whoami = new("Auth", "Whoami");

    /// <summary>
    /// Home Assistant (github.com/lukislp/studylife-hacs, not in this repo - fetched and read
    /// directly from GitHub for this audit). `custom_components/studylife/api.py` polls
    /// sessions/settings/notes/coursegoals/courses/studyprograms/timerstate every scan interval
    /// (plus the active custom programme's studyprograms/{id} detail, added for audit finding D4),
    /// and `services.py` additionally issues writes for all six `studylife.*` HA services
    /// (create/update/delete_session, set_course_goal, generate_exam_plan, set_active_program -
    /// the last one PUTs the full settings DTO via `async_update_settings`). This is by far the
    /// widest slot because Home Assistant is the one integration that manages the calendar and
    /// settings on the user's behalf, not just an AI/notes consumer.
    /// </summary>
    private static readonly HashSet<Endpoint> Ha =
    [
        Whoami,
        new("Sessions", "GetAll"), // GET /api/sessions - api.py async_get_sessions (coordinator poll)
        new("Sessions", "GetHistory"), // GET /api/sessions/history - api.py async_get_session_history (streak/quota calc)
        new("Sessions", "Create"), // POST /api/sessions - api.py async_create_session <- services.py handle_create_session
        new("Sessions", "Update"), // PUT /api/sessions/{id} - api.py async_update_session <- services.py handle_update_session
        new("Sessions", "Delete"), // DELETE /api/sessions/{id} - api.py async_delete_session <- services.py handle_delete_session
        new("Settings", "Get"), // GET /api/settings - api.py async_get_settings (coordinator poll)
        new("Settings", "Save"), // PUT /api/settings - api.py async_update_settings <- services.py handle_set_active_program
        new("Notes", "GetAll"), // GET /api/notes - api.py async_get_notes (coordinator poll)
        new("CourseGoals", "GetAll"), // GET /api/coursegoals - api.py async_get_course_goals (coordinator poll)
        new("CourseGoals", "Save"), // PUT /api/coursegoals/{courseId} - api.py async_set_course_goal <- services.py handle_set_course_goal
        new("Courses", "GetAll"), // GET /api/courses(?program=) - api.py async_get_courses (coordinator poll + course resolution)
        new("StudyPrograms", "GetAll"), // GET /api/studyprograms - api.py async_get_study_programs (coordinator poll)
        new("StudyPrograms", "Get"), // GET /api/studyprograms/{id} - api.py async_get_study_program(), called once per poll
        // cycle by coordinator.py's _async_update_data for the currently ACTIVE study programme when it's a custom one -
        // the authoritative source (StudyProgramDetailDto.GroupEctsQuotas) for a custom programme's elective-group ECTS
        // quota, replacing a "(N ECTS)"-in-the-name regex parse that silently produced an uncapped sum whenever a
        // group's display name didn't embed that convention (audit finding D4; studylife-hacs
        // fix/coordinator-week-bound-and-ects).
        new("TimerState", "Get"), // GET /api/timerstate - api.py async_get_timer_state (coordinator poll)
        new("Planner", "GenerateExamPlan"), // POST /api/planner/exam-plan - api.py async_generate_exam_plan <- services.py handle_generate_exam_plan
        // Metrics API (docs/api/metrics-contract-v1): coordinator.py's _async_update_data polls
        // both once per cycle instead of computing streak/quota/forecast/course-hours/achievements
        // etc. itself from the raw sessions/settings/courses polls above - the server is now the
        // single source of truth for every one of those numbers (see MetricsController /
        // StudyLife.Shared/StudyMetrics.cs), the HA sensors just read and display them.
        new("Metrics", "GetSummary"), // GET /api/metrics/summary(?program=&now=) - api.py async_get_metrics_summary (coordinator poll)
        new("Metrics", "GetAchievements"), // GET /api/metrics/achievements(?program=) - api.py async_get_metrics_achievements (coordinator poll)
    ];

    /// <summary>
    /// studylife-ai (studylife-ai/src/studylife_ai/studylife/client.py, StudyLifeClient) -
    /// ingestion/RAG worker that reads notes/courses/sessions/goals for its own index and writes
    /// sessions/notes as agent-tool side effects. NOT the same as the live browser chat path
    /// (AiProxyController, api/ai/*), which is SessionOnly and never reaches this map at all -
    /// this slot is exclusively the standalone worker's own X-Api-Key credential.
    /// </summary>
    private static readonly HashSet<Endpoint> Ai =
    [
        Whoami,
        new("Notes", "GetAll"), // GET /api/notes - client.py get_notes
        new("Courses", "GetAll"), // GET /api/courses - client.py get_courses
        new("Sessions", "GetHistory"), // GET /api/sessions/history - client.py get_sessions_history
        new("CourseGoals", "GetAll"), // GET /api/coursegoals - client.py get_course_goals
        new("Sessions", "Create"), // POST /api/sessions - client.py create_session
        new("Notes", "Create"), // POST /api/notes - client.py create_note
    ];

    /// <summary>
    /// studylife-mcp (studylife-mcp/src/studylife_mcp/client.py, StudyLifeClient) - MCP tool
    /// server exposing read/write StudyLife access to an LLM client (Claude Desktop etc.).
    /// exchange_mcp_assertion (POST /api/auth/mcp-assertion-exchange) is a separate, exempt
    /// server-to-server credential (the assertion itself), never an X-Api-Key request, so it
    /// has no entry here.
    /// </summary>
    private static readonly HashSet<Endpoint> Mcp =
    [
        Whoami,
        new("Courses", "GetAll"), // GET /api/courses - client.py list_courses
        new("Notes", "GetAll"), // GET /api/notes - client.py list_notes
        new("Notes", "Search"), // GET /api/notes/search - client.py search_notes
        new("Sessions", "GetAll"), // GET /api/sessions - client.py list_sessions
        new("CourseGoals", "GetAll"), // GET /api/coursegoals - client.py list_course_goals
        new("Notes", "Create"), // POST /api/notes - client.py create_note
        new("Sessions", "Create"), // POST /api/sessions - client.py create_session
    ];

    /// <summary>
    /// studylife-capture (studylife-capture/src/api.ts) - the browser extension. Deliberately
    /// the NARROWEST slot: it only ever needs to drop a captured page into a note and verify its
    /// own credentials still work, never read the rest of the account. This is precisely the gap
    /// the audit finding called out (a leaked capture key granting the full /api surface).
    /// </summary>
    private static readonly HashSet<Endpoint> Capture =
    [
        Whoami,
        new("Notes", "GetAll"), // GET /api/notes - api.ts testConnection (connection test after saving settings)
        new("Notes", "Create"), // POST /api/notes - api.ts saveCapture
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<Endpoint>> BySlot =
        new Dictionary<string, IReadOnlySet<Endpoint>>
        {
            ["ha"] = Ha,
            ["ai"] = Ai,
            ["mcp"] = Mcp,
            ["capture"] = Capture,
        };
}
