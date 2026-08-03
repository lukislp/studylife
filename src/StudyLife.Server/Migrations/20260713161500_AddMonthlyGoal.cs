using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyGoal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MonthlyGoalMaxHours",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 130);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyGoalMinHours",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthlyGoalMaxHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MonthlyGoalMinHours",
                table: "Settings");
        }
    }
}
