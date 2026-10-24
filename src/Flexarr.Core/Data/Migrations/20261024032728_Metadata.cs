using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Metadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArrId",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackdropUrl",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CommunityRating",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentRating",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CriticRating",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataSource",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataUpdatedAt",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTitle",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Overview",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Popularity",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosterUrl",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleaseDate",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RuntimeMinutes",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Studio",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbUrl",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrailerYouTubeId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TvMazeId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Genres",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Genres", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    TvMazeId = table.Column<string>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaItemGenres",
                columns: table => new
                {
                    GenresId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItemGenres", x => new { x.GenresId, x.ItemsId });
                    table.ForeignKey(
                        name: "FK_MediaItemGenres_Genres_GenresId",
                        column: x => x.GenresId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaItemGenres_MediaItems_ItemsId",
                        column: x => x.ItemsId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Credits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Character = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Credits_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Credits_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_AddedAt",
                table: "MediaItems",
                column: "AddedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Credits_MediaItemId",
                table: "Credits",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Credits_PersonId",
                table: "Credits",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Genres_Name",
                table: "Genres",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemGenres_ItemsId",
                table: "MediaItemGenres",
                column: "ItemsId");

            migrationBuilder.CreateIndex(
                name: "IX_People_Name",
                table: "People",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_People_TmdbId",
                table: "People",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_People_TvMazeId",
                table: "People",
                column: "TvMazeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Credits");

            migrationBuilder.DropTable(
                name: "MediaItemGenres");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "Genres");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_AddedAt",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AccentColor",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ArrId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "BackdropUrl",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CommunityRating",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ContentRating",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CriticRating",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "MetadataSource",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "MetadataUpdatedAt",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "OriginalTitle",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Overview",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Popularity",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "PosterUrl",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "RuntimeMinutes",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Studio",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ThumbUrl",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "TrailerYouTubeId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "TvMazeId",
                table: "MediaItems");
        }
    }
}
