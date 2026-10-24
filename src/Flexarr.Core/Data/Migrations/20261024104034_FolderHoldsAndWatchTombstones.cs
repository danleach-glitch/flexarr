using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FolderHoldsAndWatchTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeldKnown",
                table: "LibraryFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HeldMissing",
                table: "LibraryFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HeldScans",
                table: "LibraryFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeldSince",
                table: "LibraryFolders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hold",
                table: "LibraryFolders",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleasedAt",
                table: "LibraryFolders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemovalConfirmed",
                table: "LibraryFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WatchTombstones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    AirDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Collections = table.Column<string>(type: "TEXT", nullable: true),
                    Playback = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchTombstones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchTombstones_ImdbId",
                table: "WatchTombstones",
                column: "ImdbId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchTombstones_TmdbId",
                table: "WatchTombstones",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchTombstones_TvdbId",
                table: "WatchTombstones",
                column: "TvdbId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchTombstones");

            migrationBuilder.DropColumn(
                name: "HeldKnown",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "HeldMissing",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "HeldScans",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "HeldSince",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "Hold",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "RemovalConfirmed",
                table: "LibraryFolders");
        }
    }
}
