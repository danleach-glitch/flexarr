using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaFileLoudness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Loudness",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Loudness",
                table: "MediaFiles");
        }
    }
}
