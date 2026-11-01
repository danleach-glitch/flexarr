using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileLayoutAndNavigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HiddenNavigation",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Layout",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                columns: new[] { "HiddenNavigation", "Layout" },
                values: new object[] { "[]", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiddenNavigation",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "Layout",
                table: "Profiles");
        }
    }
}
