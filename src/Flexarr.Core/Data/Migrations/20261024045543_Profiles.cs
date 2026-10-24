using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Profiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Profiles",
                columns: new[] { "Id", "Color", "CreatedAt", "Name", "PinHash" },
                values: new object[] { "owner", "#a9c2ec", new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Utc), "Main profile", null });

            migrationBuilder.AddForeignKey(
                name: "FK_PlaybackStates_Profiles_UserId",
                table: "PlaybackStates",
                column: "UserId",
                principalTable: "Profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaybackStates_Profiles_UserId",
                table: "PlaybackStates");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
