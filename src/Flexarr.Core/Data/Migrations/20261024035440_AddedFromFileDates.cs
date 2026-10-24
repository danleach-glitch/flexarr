using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedFromFileDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Date added" becomes the file's modified time (when it was imported), not when Flexarr first scanned it.
            migrationBuilder.Sql("""
                UPDATE MediaItems SET AddedAt = (SELECT MIN(f.ModifiedAt) FROM MediaFiles f WHERE f.MediaItemId = MediaItems.Id)
                WHERE Kind IN ('Movie', 'Episode') AND EXISTS (SELECT 1 FROM MediaFiles f WHERE f.MediaItemId = MediaItems.Id);
                UPDATE MediaItems SET AddedAt = (SELECT MIN(e.AddedAt) FROM MediaItems e WHERE e.ParentId = MediaItems.Id)
                WHERE Kind = 'Season' AND EXISTS (SELECT 1 FROM MediaItems e WHERE e.ParentId = MediaItems.Id);
                UPDATE MediaItems SET AddedAt = (SELECT MIN(s.AddedAt) FROM MediaItems s WHERE s.ParentId = MediaItems.Id)
                WHERE Kind = 'Series' AND EXISTS (SELECT 1 FROM MediaItems s WHERE s.ParentId = MediaItems.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
