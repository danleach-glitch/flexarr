using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <summary>
    /// The Audiobooks migration gave every profile but the main one a listening speed of 0, 0-second skips and smart
    /// rewind off. Neither 0 is a value anyone can save (speeds start at 0.5×, skips at 10 seconds), so a 0 means the
    /// setting was never chosen: give it the default. Smart rewind is put back on only where nothing has been chosen
    /// at all, as an untouched profile still has its speed and both skips at 0.
    /// </summary>
    public partial class AudiobookProfileDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite works out every SET from the row as it was, so the smart rewind check sees the old zeros.
            migrationBuilder.Sql("""
                UPDATE Profiles SET
                    AudiobookSmartRewind = CASE
                        WHEN AudiobookSpeed <= 0 AND AudiobookSkipBackSeconds <= 0 AND AudiobookSkipForwardSeconds <= 0
                        THEN 1 ELSE AudiobookSmartRewind END,
                    AudiobookSpeed = CASE WHEN AudiobookSpeed <= 0 THEN 1.0 ELSE AudiobookSpeed END,
                    AudiobookSkipBackSeconds = CASE WHEN AudiobookSkipBackSeconds <= 0 THEN 30 ELSE AudiobookSkipBackSeconds END,
                    AudiobookSkipForwardSeconds = CASE WHEN AudiobookSkipForwardSeconds <= 0 THEN 30 ELSE AudiobookSkipForwardSeconds END
                WHERE AudiobookSpeed <= 0 OR AudiobookSkipBackSeconds <= 0 OR AudiobookSkipForwardSeconds <= 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the defaults are valid settings.
        }
    }
}
