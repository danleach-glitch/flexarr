using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookArrInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArrInstance",
                table: "BookDetails",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // Until now the app a book came from was only its MetadataSource.
            migrationBuilder.Sql("""
                UPDATE "BookDetails" SET "ArrInstance" = (SELECT "MetadataSource" FROM "MediaItems" WHERE "MediaItems"."Id" = "BookDetails"."MediaItemId")
                WHERE "ArrBookId" IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArrInstance",
                table: "BookDetails");
        }
    }
}
