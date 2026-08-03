using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Full-text search over notes (title + content), implemented per provider: SQLite uses FTS5
/// (see <see cref="SqliteFts5SearchStrategy"/>), Postgres uses tsvector/tsquery (see
/// <see cref="PostgresTsvectorSearchStrategy"/>) - both approaches use raw but parameterized SQL
/// and can't be expressed as a normal EF LINQ query. The calling <c>NotesController</c> notices
/// nothing about the active provider, analogous to the StudyLifeDb/StudyLifeDbSqlite/
/// StudyLifeDbPostgres pattern. The global query filters from StudyLifeDb.OnModelCreating
/// (AuthUserId) automatically apply to FromSqlRaw queries on NoteEntity too, as long as the
/// query stays composable (no manual filtering needed in the implementations - just like the
/// rest of the app).
/// </summary>
public interface INoteSearchStrategy
{
    Task<List<NoteEntity>> SearchAsync(StudyLifeDb db, string query);
}
