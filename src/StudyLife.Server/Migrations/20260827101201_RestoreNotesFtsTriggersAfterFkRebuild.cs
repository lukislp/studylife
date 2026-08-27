using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <summary>
    /// SQLite-only (no Postgres twin - same as AddNotesFts itself, which this migration repairs
    /// after: neither touches the shared EF model, so `has-pending-model-changes` doesn't
    /// require a matching migration on the Postgres side).
    ///
    /// Migration AddReferentialIntegrityForeignKeys added a real FK to Notes.SessionId, which on
    /// SQLite requires rebuilding the Notes table (DROP + CREATE + copy + rename - SQLite has no
    /// ALTER TABLE ADD CONSTRAINT for foreign keys). SQLite automatically drops every trigger
    /// defined on a table when that table is dropped, so the rebuild silently destroyed the three
    /// FTS5 sync triggers from migration AddNotesFts (Notes_fts_ai/_ad/_au) - exactly the failure
    /// mode that migration's own doc comment warns about ("anyone who rebuilds the Notes table
    /// must carry the triggers along in the same migration - EF doesn't do that automatically").
    ///
    /// This has to be its OWN, separate migration rather than added inline to
    /// AddReferentialIntegrityForeignKeys: EF's SQLite migrations generator defers the actual
    /// table rebuild until after every raw Sql() operation in a migration has already executed at
    /// its literal position (confirmed via `dotnet ef migrations script`, which even warns
    /// "operation of type SqlOperation will be attempted while a rebuild of table 'Notes' is
    /// pending" for exactly this case) - so a CREATE TRIGGER placed anywhere inside that other
    /// migration either collides with the still-live old triggers (runs before the rebuild) or
    /// gets silently destroyed again by the rebuild's own DROP TABLE (runs after, but still
    /// within the same migration's operation list, i.e. before the rebuild actually executes).
    /// By the time THIS migration runs, AddReferentialIntegrityForeignKeys is fully applied and
    /// committed, so the triggers created here stick. NotesFts itself (the external-content
    /// virtual table) was never touched by the rebuild - only the triggers needed recreating,
    /// not the index data (rowids are unchanged: the rebuild's INSERT...SELECT preserves Id).
    /// </summary>
    public partial class RestoreNotesFtsTriggersAfterFkRebuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TRIGGER Notes_fts_ai AFTER INSERT ON Notes BEGIN
    INSERT INTO NotesFts(rowid, Title, Content) VALUES (new.Id, new.Title, new.Content);
END;");

            migrationBuilder.Sql(@"
CREATE TRIGGER Notes_fts_ad AFTER DELETE ON Notes BEGIN
    INSERT INTO NotesFts(NotesFts, rowid, Title, Content) VALUES ('delete', old.Id, old.Title, old.Content);
END;");

            migrationBuilder.Sql(@"
CREATE TRIGGER Notes_fts_au AFTER UPDATE ON Notes BEGIN
    INSERT INTO NotesFts(NotesFts, rowid, Title, Content) VALUES ('delete', old.Id, old.Title, old.Content);
    INSERT INTO NotesFts(rowid, Title, Content) VALUES (new.Id, new.Title, new.Content);
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty, with a known, accepted gap: migrations roll back in reverse
            // order, so THIS Down() runs first (while the triggers from this migration's Up()
            // still exist), and AddReferentialIntegrityForeignKeys.Down() runs after - which
            // rebuilds Notes again and destroys them a second time, this time with nothing left
            // to restore them (that migration doesn't know about the trigger problem at all, by
            // design - see this migration's class comment on why the fix couldn't live there).
            // Net effect of rolling back both migrations together: Notes ends up WITHOUT the
            // AddNotesFts triggers, even though that migration's own row is still recorded as
            // applied. Not fixed here because nothing in this codebase's tests or CI ever
            // exercises a migration rollback (`Database.Migrate()` only ever moves forward,
            // Program.cs) - a real rollback is a rare, manual operation, and closing this gap
            // would need its own careful design rather than a rushed fix bolted on here.
        }
    }
}
