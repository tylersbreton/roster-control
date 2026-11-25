using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RosterControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchupRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Matchups_ChampionId_OpponentId",
                table: "Matchups");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Matchups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Matchups_ChampionId_OpponentId_Role",
                table: "Matchups",
                columns: new[] { "ChampionId", "OpponentId", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Matchups_ChampionId_OpponentId_Role",
                table: "Matchups");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Matchups");

            migrationBuilder.CreateIndex(
                name: "IX_Matchups_ChampionId_OpponentId",
                table: "Matchups",
                columns: new[] { "ChampionId", "OpponentId" },
                unique: true);
        }
    }
}
