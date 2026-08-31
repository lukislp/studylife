namespace StudyLife.Client.Services;

/// <summary>
/// Friendly display labels for the scopes a marketplace add-on can request. Mirrors
/// ApiKeyScopes.PubliclyGrantable (StudyLife.Server), studylife-marketplace's own
/// schema/known-scopes.json, and studylife-developers' own ScopeCatalog.cs - the same list is
/// hand-kept in sync across all of them; update together whenever a new scope is exposed to
/// dynamic clients. A scope missing from this map (e.g. this instance is older than the
/// marketplace listing) falls back to showing its raw "Controller.Action" id.
/// </summary>
public static class MarketplaceScopeLabels
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Notes.GetAll"] = "Read notes",
        ["Notes.Search"] = "Search notes",
        ["Notes.Create"] = "Create notes",
        ["Notes.Update"] = "Edit notes",
        ["Notes.Delete"] = "Delete notes",
        ["Sessions.GetAll"] = "Read sessions",
        ["Sessions.GetHistory"] = "Read session history",
        ["Sessions.Create"] = "Create sessions",
        ["Sessions.Update"] = "Edit sessions",
        ["Sessions.Delete"] = "Delete sessions",
        ["CourseGoals.GetAll"] = "Read course goals",
        ["CourseGoals.Save"] = "Set course goals",
        ["CourseGoals.Delete"] = "Delete course goals",
        ["TimerState.Get"] = "Read live timer state",
        ["Courses.GetAll"] = "Read the course catalog",
        ["StudyPrograms.GetAll"] = "Read study programs",
        ["StudyPrograms.Get"] = "Read a study program's detail",
        ["Metrics.GetSummary"] = "Read metrics summary",
        ["WebhooksProxy.List"] = "List webhook registrations",
        ["WebhooksProxy.Create"] = "Create webhook registrations",
        ["WebhooksProxy.Delete"] = "Delete webhook registrations",
    };

    public static string Describe(string scopeId) => Labels.GetValueOrDefault(scopeId, scopeId);
}
