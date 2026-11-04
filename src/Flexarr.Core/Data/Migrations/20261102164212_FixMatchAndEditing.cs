using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixMatchAndEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ArtworkLocked",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DeezerId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DetailsLocked",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MusicAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicAliases_MediaItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicAliases_ItemId",
                table: "MusicAliases",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAliases_LibraryId_Key",
                table: "MusicAliases",
                columns: new[] { "LibraryId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusicAliases");

            migrationBuilder.DropColumn(
                name: "ArtworkLocked",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DeezerId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DetailsLocked",
                table: "MediaItems");
        }
    }
}
