using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: safe on both fresh and existing databases
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "Sessions" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY AUTOINCREMENT,
                    "CourseId" INTEGER NOT NULL,
                    "CourseName" TEXT NOT NULL,
                    "CourseColor" TEXT NOT NULL,
                    "StartTime" TEXT NOT NULL,
                    "EndTime" TEXT NOT NULL,
                    "Topic" TEXT NULL,
                    "Notes" TEXT NULL,
                    "IsCompleted" INTEGER NOT NULL,
                    "TimerModeId" INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS "Settings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY AUTOINCREMENT,
                    "SelectedCourseIds" TEXT NOT NULL DEFAULT '1,2,3,4',
                    "Theme" TEXT NOT NULL DEFAULT 'dark',
                    "AutoSwitchFocus" INTEGER NOT NULL DEFAULT 1,
                    "AutoSwitchMinutesBefore" INTEGER NOT NULL DEFAULT 2,
                    "MotivationalStyle" TEXT NOT NULL DEFAULT 'claude'
                );
                CREATE TABLE IF NOT EXISTS "PushSubscriptions" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PushSubscriptions" PRIMARY KEY AUTOINCREMENT,
                    "Endpoint" TEXT NOT NULL,
                    "P256dh" TEXT NOT NULL,
                    "Auth" TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS "SentReminders" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SentReminders" PRIMARY KEY AUTOINCREMENT,
                    "Key" TEXT NOT NULL,
                    "SentAt" TEXT NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "SentReminders";
                DROP TABLE IF EXISTS "PushSubscriptions";
                DROP TABLE IF EXISTS "Settings";
                DROP TABLE IF EXISTS "Sessions";
                """);
        }
    }
}
