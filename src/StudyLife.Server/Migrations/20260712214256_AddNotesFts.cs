using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <summary>
    /// SQLite FTS5 full-text index over Notes(Title, Content) as an external content table
    /// (content='Notes'), plus the three standard sync triggers. Deliberately a pure
    /// raw-SQL migration: NotesFts is NOT an EF entity and must not appear in the model
    /// snapshot - EF continues to manage only the normal Notes table.
    /// </summary>
    public partial class AddNotesFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE NotesFts USING fts5(Title, Content, content='Notes', content_rowid='Id');");

            // Initially index existing notes.
            migrationBuilder.Sql(
                "INSERT INTO NotesFts(rowid, Title, Content) SELECT Id, Title, Content FROM Notes;");

            // External-content sync triggers following the documented fts5 pattern
            // (https://www.sqlite.org/fts5.html#external_content_tables): delete/update
            // use the special 'delete' command, which needs the old row content.
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
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Notes_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS NotesFts;");
        }
    }
}
