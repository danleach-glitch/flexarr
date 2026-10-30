using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KidsProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KidsOverride",
                table: "WatchTombstones",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "IsKids",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxRatingAge",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KidsOverride",
                table: "MediaItems",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "RatingAge",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FineForKids",
                table: "LiveChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FineForKids",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                columns: new[] { "IsKids", "MaxRatingAge" },
                values: new object[] { false, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KidsOverride",
                table: "WatchTombstones");

            migrationBuilder.DropColumn(
                name: "IsKids",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "MaxRatingAge",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "KidsOverride",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "RatingAge",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "FineForKids",
                table: "LiveChannels");

            migrationBuilder.DropColumn(
                name: "FineForKids",
                table: "Libraries");
        }
    }
}
