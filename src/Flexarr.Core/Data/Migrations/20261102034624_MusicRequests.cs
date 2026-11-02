using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequestMusicLibraryId",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtistMusicBrainzId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtistName",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                column: "RequestMusicLibraryId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_FulfilledAt_Kind_MusicBrainzId",
                table: "MediaRequests",
                columns: new[] { "FulfilledAt", "Kind", "MusicBrainzId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_FulfilledAt_Kind_MusicBrainzId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "RequestMusicLibraryId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistMusicBrainzId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ArtistName",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "MediaRequests");
        }
    }
}
