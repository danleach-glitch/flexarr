using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FulfilledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaRequests_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationCursors",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Through = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationCursors", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDevices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SignInId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Key = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Films = table.Column<bool>(type: "INTEGER", nullable: false),
                    Shows = table.Column<bool>(type: "INTEGER", nullable: false),
                    Podcasts = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recordings = table.Column<bool>(type: "INTEGER", nullable: false),
                    Requests = table.Column<bool>(type: "INTEGER", nullable: false),
                    Upcoming = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpcomingHour = table.Column<int>(type: "INTEGER", nullable: false),
                    UpcomingSentOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDevices_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_FulfilledAt_Kind_TmdbId",
                table: "MediaRequests",
                columns: new[] { "FulfilledAt", "Kind", "TmdbId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_ProfileId",
                table: "MediaRequests",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDevices_ProfileId",
                table: "NotificationDevices",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDevices_Token",
                table: "NotificationDevices",
                column: "Token");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaRequests");

            migrationBuilder.DropTable(
                name: "NotificationCursors");

            migrationBuilder.DropTable(
                name: "NotificationDevices");
        }
    }
}
