using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TitleVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TitleGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleGroups", x => x.Id);
                });

            // Added with its reference in one go: AddForeignKey would make SQLite copy the whole table, every file's title,
            // on everyone's first start, and lose its columns' defaults.
            migrationBuilder.Sql("""ALTER TABLE "MediaItems" ADD COLUMN "LinkGroupId" TEXT NULL REFERENCES "TitleGroups" ("Id") ON DELETE SET NULL;""");

            migrationBuilder.CreateTable(
                name: "VersionChoices",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Edition = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ChosenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionChoices", x => new { x.ProfileId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_VersionChoices_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VersionChoices_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_LinkGroupId",
                table: "MediaItems",
                column: "LinkGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_VersionChoices_MediaItemId",
                table: "VersionChoices",
                column: "MediaItemId");

        
            // Episodes' other versions were marked as scanned without being looked at. Now they're looked at.
            migrationBuilder.Sql("""
                UPDATE "MediaFiles" SET "MarkersScannedAt" = NULL
                WHERE "MarkersScannedAt" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "MediaMarkers" m WHERE m."MediaFileId" = "MediaFiles"."Id")
                  AND "MediaItemId" IN (
                    SELECT f."MediaItemId" FROM "MediaFiles" f JOIN "MediaItems" i ON i."Id" = f."MediaItemId"
                    WHERE i."Kind" = 'Episode' GROUP BY f."MediaItemId" HAVING COUNT(*) > 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_TitleGroups_LinkGroupId",
                table: "MediaItems");

            migrationBuilder.DropTable(
                name: "TitleGroups");

            migrationBuilder.DropTable(
                name: "VersionChoices");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_LinkGroupId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LinkGroupId",
                table: "MediaItems");
        }
    }
}
