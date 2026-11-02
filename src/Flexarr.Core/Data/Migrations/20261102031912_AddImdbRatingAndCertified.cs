using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImdbRatingAndCertified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CriticCertified",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "ImdbRating",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            // Movies and shows are looked up again at the next scan, so their IMDb score and Certified Fresh fill in
            // straight away rather than over the week it takes them to go stale.
            migrationBuilder.Sql("UPDATE MediaItems SET MetadataUpdatedAt = NULL WHERE Kind IN ('Movie', 'Series') AND ParentId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CriticCertified",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ImdbRating",
                table: "MediaItems");
        }
    }
}
