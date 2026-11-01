using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <summary>
    /// Podcasts and books now share one speed. Where a profile's two speeds differ, the one its owner chose wins: a
    /// podcast speed other than 1× (podcasts came first, so it's the one more likely chosen), otherwise a book speed
    /// other than 1×. Books keep their speeds to the nearest 0.05×, from 0.5× to 3.5×.
    /// </summary>
    public partial class ShareListeningSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Profiles SET AudiobookSpeed = MIN(MAX(ROUND(PodcastSpeed * 20) / 20.0, 0.5), 3.5)
                    WHERE PodcastSpeed > 0 AND PodcastSpeed <> 1;
                UPDATE Profiles SET PodcastSpeed = AudiobookSpeed
                    WHERE PodcastSpeed = 1 AND AudiobookSpeed > 0 AND AudiobookSpeed <> 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: both speeds are valid settings.
        }
    }
}
