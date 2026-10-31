using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Audiobooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudiobookSkipBackSeconds",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AudiobookSkipForwardSeconds",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookSmartRewind",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "AudiobookSpeed",
                table: "Profiles",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "PlaybackStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Asin",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Biography",
                table: "People",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BookOrder",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookDetails",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: true),
                    Asin = table.Column<string>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Abridged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Explicit = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    ArrBookId = table.Column<string>(type: "TEXT", nullable: true),
                    ArrAuthorId = table.Column<string>(type: "TEXT", nullable: true),
                    ChapterSource = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookDetails", x => x.MediaItemId);
                    table.ForeignKey(
                        name: "FK_BookDetails_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Bookmarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PositionSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookmarks_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Bookmarks_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookSeries_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaChapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EndSeconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaChapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaChapters_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookSeriesEntries",
                columns: table => new
                {
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookSeriesEntries", x => new { x.SeriesId, x.BookId });
                    table.ForeignKey(
                        name: "FK_BookSeriesEntries_BookSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "BookSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookSeriesEntries_MediaItems_BookId",
                        column: x => x.BookId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Profiles",
                keyColumn: "Id",
                keyValue: "owner",
                columns: new[] { "AudiobookSkipBackSeconds", "AudiobookSkipForwardSeconds", "AudiobookSmartRewind", "AudiobookSpeed" },
                values: new object[] { 30, 30, true, 1.0 });

            migrationBuilder.CreateIndex(
                name: "IX_BookDetails_ArrBookId",
                table: "BookDetails",
                column: "ArrBookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookDetails_Asin",
                table: "BookDetails",
                column: "Asin");

            migrationBuilder.CreateIndex(
                name: "IX_BookDetails_Isbn",
                table: "BookDetails",
                column: "Isbn");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_MediaItemId",
                table: "Bookmarks",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_ProfileId_MediaItemId",
                table: "Bookmarks",
                columns: new[] { "ProfileId", "MediaItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_BookSeries_LibraryId_SortName",
                table: "BookSeries",
                columns: new[] { "LibraryId", "SortName" });

            migrationBuilder.CreateIndex(
                name: "IX_BookSeriesEntries_BookId",
                table: "BookSeriesEntries",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaChapters_MediaItemId_Index",
                table: "MediaChapters",
                columns: new[] { "MediaItemId", "Index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookDetails");

            migrationBuilder.DropTable(
                name: "Bookmarks");

            migrationBuilder.DropTable(
                name: "BookSeriesEntries");

            migrationBuilder.DropTable(
                name: "MediaChapters");

            migrationBuilder.DropTable(
                name: "BookSeries");

            migrationBuilder.DropColumn(
                name: "AudiobookSkipBackSeconds",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "AudiobookSkipForwardSeconds",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "AudiobookSmartRewind",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "AudiobookSpeed",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "PlaybackStates");

            migrationBuilder.DropColumn(
                name: "Asin",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Biography",
                table: "People");

            migrationBuilder.DropColumn(
                name: "BookOrder",
                table: "MediaFiles");
        }
    }
}
