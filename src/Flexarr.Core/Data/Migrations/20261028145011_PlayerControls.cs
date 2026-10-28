using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayerControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AudioDelaySeconds",
                table: "MediaFiles",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "ShowPlaybackSpeeds",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Speed = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowPlaybackSpeeds", x => new { x.ProfileId, x.SeriesId });
                    table.ForeignKey(
                        name: "FK_ShowPlaybackSpeeds_MediaItems_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShowPlaybackSpeeds_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShowPlaybackSpeeds_SeriesId",
                table: "ShowPlaybackSpeeds",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShowPlaybackSpeeds");

            migrationBuilder.DropColumn(
                name: "AudioDelaySeconds",
                table: "MediaFiles");
        }
    }
}
