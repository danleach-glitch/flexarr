using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaItemExternalIdIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ImdbId",
                table: "MediaItems",
                column: "ImdbId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_TmdbId",
                table: "MediaItems",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_TvdbId",
                table: "MediaItems",
                column: "TvdbId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_ImdbId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_TmdbId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_TvdbId",
                table: "MediaItems");
        }
    }
}
