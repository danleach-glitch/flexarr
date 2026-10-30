using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecentSearches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecentSearches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SearchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecentSearches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecentSearches_MediaItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecentSearches_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecentSearches_ItemId",
                table: "RecentSearches",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RecentSearches_ProfileId_SearchedAt",
                table: "RecentSearches",
                columns: new[] { "ProfileId", "SearchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecentSearches");
        }
    }
}
