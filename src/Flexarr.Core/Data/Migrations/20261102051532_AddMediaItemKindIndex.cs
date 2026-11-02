using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItemKindIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_LibraryId_Kind_SortTitle",
                table: "MediaItems",
                columns: new[] { "LibraryId", "Kind", "SortTitle" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_LibraryId_Kind_SortTitle",
                table: "MediaItems");
        }
    }
}
