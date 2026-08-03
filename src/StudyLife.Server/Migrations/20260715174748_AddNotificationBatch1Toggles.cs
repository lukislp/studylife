using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationBatch1Toggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BestStudyTimeRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CourseAlmostDoneRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StreakRiskRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyGoalNudgeEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BestStudyTimeRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CourseAlmostDoneRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StreakRiskRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WeeklyGoalNudgeEnabled",
                table: "Settings");
        }
    }
}
