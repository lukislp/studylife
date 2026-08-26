namespace StudyLife.Server.Services;

/// <summary>
/// Tolerant parser for the comma-separated int-id columns used throughout the schema
/// (UserSettingsEntity.SelectedCourseIds/CompletedCourseIds, NoteEntity.RelatedNoteIds, ...).
/// Unlike a bare "s.Split(',').Select(int.Parse)", a single malformed token here never throws -
/// it is skipped and parsing continues with the remaining tokens. Without this, one bad entry
/// (e.g. written by an external service like studylife-ai's capture enrichment) would make every
/// subsequent GET of the owning row throw 500 permanently, since the data stays poisoned until
/// someone edits it back through a write path that doesn't validate either.
/// </summary>
public static class CommaSeparatedIds
{
    public static List<int> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new List<int>();
        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? (int?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
    }
}
