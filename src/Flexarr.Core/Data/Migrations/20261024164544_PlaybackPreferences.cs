using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing profile starts with auto-play on, as new ones do.
            migrationBuilder.AddColumn<bool>(
                name: "AutoPlayNext",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipCredits",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipIntros",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipRecaps",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                columns: new[] { "AutoPlayNext", "SkipCredits", "SkipIntros", "SkipRecaps" },
                values: new object[] { true, false, false, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPlayNext",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SkipCredits",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SkipIntros",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SkipRecaps",
                table: "Profiles");
        }
    }
}
