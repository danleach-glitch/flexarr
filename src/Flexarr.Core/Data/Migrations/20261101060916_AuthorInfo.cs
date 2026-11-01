using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthorInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BiographySource",
                table: "People",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImageChangedAt",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageStamp",
                table: "People",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InfoCheckedAt",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InfoLocked",
                table: "People",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PhotoSource",
                table: "People",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BiographySource",
                table: "People");

            migrationBuilder.DropColumn(
                name: "ImageChangedAt",
                table: "People");

            migrationBuilder.DropColumn(
                name: "ImageStamp",
                table: "People");

            migrationBuilder.DropColumn(
                name: "InfoCheckedAt",
                table: "People");

            migrationBuilder.DropColumn(
                name: "InfoLocked",
                table: "People");

            migrationBuilder.DropColumn(
                name: "PhotoSource",
                table: "People");
        }
    }
}
