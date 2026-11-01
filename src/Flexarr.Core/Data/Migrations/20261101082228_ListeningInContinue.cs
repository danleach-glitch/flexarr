using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <summary>
    /// Every profile, not just the main one, starts with books and podcasts in Continue watching: the column's default
    /// is true, so existing rows get it too.
    /// </summary>
    public partial class ListeningInContinue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ListeningInContinue",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListeningInContinue",
                table: "Profiles");
        }
    }
}
