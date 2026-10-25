using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubtitleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubtitleSize",
                table: "Profiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<double>(
                name: "SubtitleDelaySeconds",
                table: "PlaybackStates",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                column: "SubtitleSize",
                value: "Standard");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitleSize",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SubtitleDelaySeconds",
                table: "PlaybackStates");
        }
    }
}
