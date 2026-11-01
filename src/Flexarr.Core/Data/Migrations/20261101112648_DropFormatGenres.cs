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
            // Written out, not taken from BookGenres: a migration must do the same thing however the code changes. Names are
            // compared as BookGenres does, on their letters only ("Audiobooks - Unabridged", "Spoken-Word").
            const string names = "'audiobook', 'audiobooks', 'audiobookunabridged', 'audiobooksunabridged', 'audio', 'audible', 'audibleaudio', "
                + "'spokenword', 'speech', 'unabridged', 'abridged', 'book', 'books'";
            const string letters = """lower(replace(replace(replace(replace(replace(replace(replace(replace("Name", ' ', ''), '-', ''), '/', ''), '_', ''), '.', ''), ',', ''), ':', ''), '&', ''))""";
            migrationBuilder.Sql($"""
                DELETE FROM "MediaItemGenres"
                WHERE "ItemsId" IN (SELECT "MediaItemId" FROM "BookDetails")
                  AND "GenresId" IN (SELECT "Id" FROM "Genres" WHERE {letters} IN ({names}));
                """);
            migrationBuilder.Sql($"""
                DELETE FROM "Genres"
                WHERE {letters} IN ({names})
                  AND "Id" NOT IN (SELECT "GenresId" FROM "MediaItemGenres");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
