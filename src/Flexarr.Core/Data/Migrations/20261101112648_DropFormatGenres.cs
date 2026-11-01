using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <summary>
    /// Books lose genres that only name the format ("Audiobook", "Audiobooks", "Spoken Word"): they split the Genres
    /// view and say nothing about the book. Films and shows keep theirs; a genre no longer used by anything goes.
    /// </summary>
    public partial class DropFormatGenres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Written out, not taken from BookGenres: a migration must do the same thing however the code changes.
            const string names = "'audiobook', 'audiobooks', 'audio book', 'audio books', 'audiobook - unabridged', 'audio', 'audible', "
                + "'audible audio', 'spoken word', 'speech', 'unabridged', 'abridged', 'book', 'books'";
            migrationBuilder.Sql($"""
                DELETE FROM "MediaItemGenres"
                WHERE "ItemsId" IN (SELECT "MediaItemId" FROM "BookDetails")
                  AND "GenresId" IN (SELECT "Id" FROM "Genres" WHERE lower("Name") IN ({names}));
                """);
            migrationBuilder.Sql($"""
                DELETE FROM "Genres"
                WHERE lower("Name") IN ({names})
                  AND "Id" NOT IN (SELECT "GenresId" FROM "MediaItemGenres");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
