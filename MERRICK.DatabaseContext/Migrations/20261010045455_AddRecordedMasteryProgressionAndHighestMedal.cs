using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordedMasteryProgressionAndHighestMedal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MasteryProgression",
                schema: "stat",
                table: "MatchParticipantStatistics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HighestMedal",
                schema: "stat",
                table: "AccountStatistics",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Preserve The Existing Visible Medal When Introducing Season-High Tracking
            migrationBuilder.Sql("""
                UPDATE [stat].[AccountStatistics]
                SET [HighestMedal] = CASE
                    WHEN [PlacementMatchesData] IS NOT NULL AND LEN([PlacementMatchesData]) < 6 THEN 0
                    WHEN [SkillRating] >= 1950 THEN 20
                    WHEN [SkillRating] >= 1900 THEN 19
                    WHEN [SkillRating] >= 1850 THEN 18
                    WHEN [SkillRating] >= 1805 THEN 17
                    WHEN [SkillRating] >= 1765 THEN 16
                    WHEN [SkillRating] >= 1725 THEN 15
                    WHEN [SkillRating] >= 1685 THEN 14
                    WHEN [SkillRating] >= 1645 THEN 13
                    WHEN [SkillRating] >= 1610 THEN 12
                    WHEN [SkillRating] >= 1575 THEN 11
                    WHEN [SkillRating] >= 1540 THEN 10
                    WHEN [SkillRating] >= 1505 THEN 9
                    WHEN [SkillRating] >= 1470 THEN 8
                    WHEN [SkillRating] >= 1435 THEN 7
                    WHEN [SkillRating] >= 1400 THEN 6
                    WHEN [SkillRating] >= 1360 THEN 5
                    WHEN [SkillRating] >= 1330 THEN 4
                    WHEN [SkillRating] >= 1300 THEN 3
                    WHEN [SkillRating] >= 1275 THEN 2
                    WHEN [SkillRating] >= 1250 THEN 1
                    ELSE 0
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MasteryProgression",
                schema: "stat",
                table: "MatchParticipantStatistics");

            migrationBuilder.DropColumn(
                name: "HighestMedal",
                schema: "stat",
                table: "AccountStatistics");
        }
    }
}
