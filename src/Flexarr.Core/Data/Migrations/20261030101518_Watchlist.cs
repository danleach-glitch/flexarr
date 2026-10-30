using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Watchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRequestWatchlist",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WatchlistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    BackdropUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ArrivedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                column: "AutoRequestWatchlist",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_ArrivedAt_Kind_TmdbId",
                table: "WatchlistEntries",
                columns: new[] { "ArrivedAt", "Kind", "TmdbId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_MediaItemId",
                table: "WatchlistEntries",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_ProfileId_Kind_TmdbId",
                table: "WatchlistEntries",
                columns: new[] { "ProfileId", "Kind", "TmdbId" },
                unique: true,
                filter: "\"TmdbId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_ProfileId_MediaItemId",
                table: "WatchlistEntries",
                columns: new[] { "ProfileId", "MediaItemId" },
                unique: true,
                filter: "\"MediaItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "AutoRequestWatchlist",
                table: "Profiles");
        }
    }
}
