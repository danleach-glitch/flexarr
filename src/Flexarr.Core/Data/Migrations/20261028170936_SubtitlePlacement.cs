using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubtitlePlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubtitlePosition",
                table: "Profiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Bottom");

            migrationBuilder.AddColumn<bool>(
                name: "SubtitlesInBlackBars",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ClosedCaptions",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                columns: new[] { "SubtitlePosition", "SubtitlesInBlackBars" },
                values: new object[] { "Bottom", false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitlePosition",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SubtitlesInBlackBars",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ClosedCaptions",
                table: "MediaFiles");
        }
    }
}
