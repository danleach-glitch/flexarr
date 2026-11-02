using System;
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

            migrationBuilder.AddColumn<DateTime>(
                name: "ScoresCheckedAt",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            // Movies and shows in Movies and TV libraries are looked up again at the next scan, so their IMDb score and
            // Certified Fresh fill in straight away rather than over the week it takes them to go stale. The Unix epoch
            // rather than NULL: they still count as having details meanwhile (as PathMappingFinder does it).
            migrationBuilder.Sql("""
                UPDATE MediaItems SET MetadataUpdatedAt = '1970-01-01 00:00:00'
                WHERE Kind IN ('Movie', 'Series') AND ParentId IS NULL AND MetadataUpdatedAt IS NOT NULL
                  AND LibraryId IN (SELECT Id FROM Libraries WHERE Type IN ('Movies', 'TvShows'));
                """);
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

            migrationBuilder.DropColumn(
                name: "ScoresCheckedAt",
                table: "MediaItems");
        }
    }
}
