using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReadarr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flexarr no longer connects to Readarr, Bookshelf or Chaptarr (never released). Before their columns go:

            // Book requests can't be answered any more.
            migrationBuilder.Sql("DELETE FROM MediaRequests WHERE Kind = 'Audiobook';");

            // Books whose details came from the app are looked up online again, without its name as their source.
            migrationBuilder.Sql("""
                UPDATE MediaItems SET MetadataSource = NULL, MetadataUpdatedAt = NULL
                WHERE Kind = 'Audiobook' AND EXISTS (
                    SELECT 1 FROM BookDetails d
                    WHERE d.MediaItemId = MediaItems.Id AND d.ArrInstance IS NOT NULL AND d.ArrInstance = MediaItems.MetadataSource);
                """);

            // Authors' photos and biographies from the app are cleared, unless an admin locked them, and looked up again
            // (folder, Audible, Open Library): due now, with one book of each, whose refresh fills them (the rest of their
            // books needn't be looked up online again for that).
            migrationBuilder.Sql("""
                UPDATE MediaItems SET MetadataUpdatedAt = NULL
                WHERE Kind = 'Audiobook' AND Id IN (
                    SELECT MIN(c.MediaItemId) FROM Credits c JOIN People p ON p.Id = c.PersonId
                    JOIN MediaItems i ON i.Id = c.MediaItemId AND i.Kind = 'Audiobook' AND i.MissingSince IS NULL
                    WHERE c.Role = 'Author' AND p.InfoLocked = 0 AND (p.PhotoSource = 'readarr' OR p.BiographySource = 'readarr')
                    GROUP BY c.PersonId);
                UPDATE People SET ImageUrl = NULL, PhotoSource = NULL, ImageStamp = NULL, ImageChangedAt = strftime('%Y-%m-%d %H:%M:%f', 'now'), InfoCheckedAt = NULL
                WHERE PhotoSource = 'readarr' AND InfoLocked = 0;
                UPDATE People SET Biography = NULL, BiographySource = NULL, InfoCheckedAt = NULL
                WHERE BiographySource = 'readarr' AND InfoLocked = 0;
                """);

            migrationBuilder.DropIndex(
                name: "IX_BookDetails_ArrBookId",
                table: "BookDetails");

            migrationBuilder.DropColumn(
                name: "RequestAudiobooksLibraryId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "BookArrId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "BookArrInstance",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "BookForeignId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ArrAuthorId",
                table: "BookDetails");

            migrationBuilder.DropColumn(
                name: "ArrBookId",
                table: "BookDetails");

            migrationBuilder.DropColumn(
                name: "ArrInstance",
                table: "BookDetails");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequestAudiobooksLibraryId",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookArrId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookArrInstance",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookForeignId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrAuthorId",
                table: "BookDetails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrBookId",
                table: "BookDetails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrInstance",
                table: "BookDetails",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                column: "RequestAudiobooksLibraryId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_BookDetails_ArrBookId",
                table: "BookDetails",
                column: "ArrBookId");
        }
    }
}
