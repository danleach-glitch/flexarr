using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Playback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MarkersScannedAt",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaMarkers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EndSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaMarkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaMarkers_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PositionSeconds = table.Column<double>(type: "REAL", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Watched = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AudioLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    SubtitleLanguage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackStates", x => new { x.UserId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_PlaybackStates_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaMarkers_MediaFileId",
                table: "MediaMarkers",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackStates_MediaItemId",
                table: "PlaybackStates",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackStates_UserId_LastPlayedAt",
                table: "PlaybackStates",
                columns: new[] { "UserId", "LastPlayedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaMarkers");

            migrationBuilder.DropTable(
                name: "PlaybackStates");

            migrationBuilder.DropColumn(
                name: "MarkersScannedAt",
                table: "MediaFiles");
        }
    }
}
