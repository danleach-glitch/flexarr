using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Trakt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OnTraktAt",
                table: "WatchlistEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraktListId",
                table: "Collections",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TraktConnections",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WatchlistFull = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastActivities = table.Column<string>(type: "TEXT", nullable: true),
                    HistoryCursor = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WatchlistSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReconciledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ListIds = table.Column<string>(type: "TEXT", nullable: false),
                    ListsSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraktConnections", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_TraktConnections_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TraktHistoryHolds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TvdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    WatchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraktHistoryHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraktHistoryHolds_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TraktOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraktOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraktOutbox_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraktConnections_UserSlug",
                table: "TraktConnections",
                column: "UserSlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraktHistoryHolds_ProfileId",
                table: "TraktHistoryHolds",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TraktOutbox_NextAttemptAt",
                table: "TraktOutbox",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_TraktOutbox_ProfileId",
                table: "TraktOutbox",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraktConnections");

            migrationBuilder.DropTable(
                name: "TraktHistoryHolds");

            migrationBuilder.DropTable(
                name: "TraktOutbox");

            migrationBuilder.DropColumn(
                name: "OnTraktAt",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "TraktListId",
                table: "Collections");
        }
    }
}
