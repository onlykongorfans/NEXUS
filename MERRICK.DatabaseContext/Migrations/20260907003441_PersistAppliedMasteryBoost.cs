using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Migrations
{
    /// <inheritdoc />
    public partial class PersistAppliedMasteryBoost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MasteryBoostExperience",
                schema: "stat",
                table: "MatchParticipantStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MasteryBoostIsSuperBoost",
                schema: "stat",
                table: "MatchParticipantStatistics",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MasteryBoostExperience",
                schema: "stat",
                table: "MatchParticipantStatistics");

            migrationBuilder.DropColumn(
                name: "MasteryBoostIsSuperBoost",
                schema: "stat",
                table: "MatchParticipantStatistics");
        }
    }
}
