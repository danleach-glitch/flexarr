using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitAdminPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminPinHash",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            // The main profile's one PIN was the admin PIN: it stays that. Whether the main profile keeps it as its own
            // PIN too depends on config.json's askPinForMainProfile, which runs at startup (see Program.cs).
            migrationBuilder.Sql("UPDATE Profiles SET AdminPinHash = PinHash WHERE Id = 'owner';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminPinHash",
                table: "Profiles");
        }
    }
}
