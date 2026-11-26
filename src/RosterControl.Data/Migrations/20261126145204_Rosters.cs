using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RosterControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rosters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-ordered: create the table and a first roster, then move every existing group into it (id 1).
            migrationBuilder.CreateTable(
                name: "Rosters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rosters", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Rosters",
                columns: ["Id", "Name", "SortOrder", "CreatedAt"],
                values: new object[] { 1, "My roster", 0, new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero) });

            migrationBuilder.AddColumn<int>(
                name: "RosterId",
                table: "RosterGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_RosterGroups_RosterId",
                table: "RosterGroups",
                column: "RosterId");

            migrationBuilder.AddForeignKey(
                name: "FK_RosterGroups_Rosters_RosterId",
                table: "RosterGroups",
                column: "RosterId",
                principalTable: "Rosters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RosterGroups_Rosters_RosterId",
                table: "RosterGroups");

            migrationBuilder.DropTable(
                name: "Rosters");

            migrationBuilder.DropIndex(
                name: "IX_RosterGroups_RosterId",
                table: "RosterGroups");

            migrationBuilder.DropColumn(
                name: "RosterId",
                table: "RosterGroups");
        }
    }
}
