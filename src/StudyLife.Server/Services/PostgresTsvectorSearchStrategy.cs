using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>
/// Full-text search via Postgres tsvector/tsquery, as the counterpart to
/// SqliteFts5SearchStrategy. Deliberately query-time computation (to_tsvector directly in the
/// WHERE/ORDER BY clause) instead of a generated tsvector column + GIN index: for this learning
/// project with very few users and a correspondingly small note volume, the missing index is
/// uncritical, and this variant needs no additional Postgres migration (no
/// Migrations/Postgres/ folder needed). If the note volume grows noticeably, a generated
/// column + GIN index would be the next step.
///
/// 'simple' text search configuration instead of 'german'/'english': the app is multilingual,
/// 'simple' avoids a wrong language assumption, but in exchange does NO stemming (e.g. "lernen"
/// doesn't automatically match "gelernt") - a deliberate trade-off against the more precise FTS5
/// prefix search on the SQLite side. plainto_tsquery instead of websearch_to_tsquery for broader
/// Postgres version compatibility (websearch_to_tsquery only exists from Postgres 11 onward).
/// </summary>
public class PostgresTsvectorSearchStrategy : INoteSearchStrategy
{
    public async Task<List<NoteEntity>> SearchAsync(StudyLifeDb db, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        return await db.Notes
            .FromSqlRaw(
                "SELECT * FROM \"Notes\" WHERE to_tsvector('simple', \"Title\" || ' ' || \"Content\") "
                + "@@ plainto_tsquery('simple', {0}) "
                + "ORDER BY ts_rank(to_tsvector('simple', \"Title\" || ' ' || \"Content\"), plainto_tsquery('simple', {0})) DESC",
                query)
            .AsNoTracking()
            .ToListAsync();
    }
}
