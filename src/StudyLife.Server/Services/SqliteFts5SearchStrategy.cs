using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Full-text search via SQLite FTS5 (external content table NotesFts, see migration
/// AddNotesFts). Result is relevance-sorted (bm25 "rank"). Moved here 1:1 from
/// NotesController.Search (scalability branch) - behavior unchanged.
/// </summary>
public class SqliteFts5SearchStrategy : INoteSearchStrategy
{
    public async Task<List<NoteEntity>> SearchAsync(StudyLifeDb db, string query)
    {
        var match = BuildFtsMatchQuery(query);
        if (match.Length == 0) return [];

        return await db.Notes
            .FromSqlRaw(
                "SELECT n.* FROM Notes n JOIN NotesFts f ON f.rowid = n.Id WHERE NotesFts MATCH {0} ORDER BY rank",
                match)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// Translates raw user input into a safe FTS5 MATCH expression: each whitespace-separated
    /// word is written as a prefix phrase ("word"*), embedded quote characters are doubled per
    /// FTS5 convention. Without this normalization, MATCH throws a syntax error on special
    /// characters (e.g. '-', '(' or a lone '"').
    /// </summary>
    private static string BuildFtsMatchQuery(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "";
        var terms = q
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*");
        return string.Join(" ", terms);
    }
}
