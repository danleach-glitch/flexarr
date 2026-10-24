using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TailscaleLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TailscaleLogin",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                column: "TailscaleLogin",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TailscaleLogin",
                table: "Profiles",
                column: "TailscaleLogin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_TailscaleLogin",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "TailscaleLogin",
                table: "Profiles");
        }
    }
}
