using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchKindTitleIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Kind_Title",
                table: "MediaItems",
                columns: new[] { "Kind", "Title" },
                filter: "\"Kind\" IN ('Episode', 'Album', 'Track', 'Audiobook')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Kind_Title",
                table: "MediaItems");
        }
    }
}
