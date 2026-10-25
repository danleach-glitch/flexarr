using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flexarr.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Podcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PodcastFeeds",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ITunesId = table.Column<long>(type: "INTEGER", nullable: true),
                    PodcastGuid = table.Column<string>(type: "TEXT", nullable: true),
                    Link = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Explicit = table.Column<bool>(type: "INTEGER", nullable: false),
                    Categories = table.Column<string>(type: "TEXT", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", nullable: true),
                    LastModified = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NewEpisodeAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextCheckAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastFeeds", x => x.MediaItemId);
                    table.ForeignKey(
                        name: "FK_PodcastFeeds_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PodcastEpisodes",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeedId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeGuid = table.Column<string>(type: "TEXT", nullable: false),
                    AudioUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", nullable: true),
                    LengthBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    EpisodeType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Explicit = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChaptersUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptType = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastEpisodes", x => x.MediaItemId);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastEpisodes_PodcastFeeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "PodcastFeeds",
                        principalColumn: "MediaItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PodcastSubscriptions",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FeedId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscribedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AutoDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaybackSpeed = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastSubscriptions", x => new { x.ProfileId, x.FeedId });
                    table.ForeignKey(
                        name: "FK_PodcastSubscriptions_PodcastFeeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "PodcastFeeds",
                        principalColumn: "MediaItemId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PodcastSubscriptions_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_FeedId_EpisodeGuid",
                table: "PodcastEpisodes",
                columns: new[] { "FeedId", "EpisodeGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastEpisodes_FeedId_PublishedAt",
                table: "PodcastEpisodes",
                columns: new[] { "FeedId", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastFeeds_ITunesId",
                table: "PodcastFeeds",
                column: "ITunesId");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastFeeds_NextCheckAt",
                table: "PodcastFeeds",
                column: "NextCheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastFeeds_Url",
                table: "PodcastFeeds",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PodcastSubscriptions_FeedId",
                table: "PodcastSubscriptions",
                column: "FeedId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PodcastEpisodes");

            migrationBuilder.DropTable(
                name: "PodcastSubscriptions");

            migrationBuilder.DropTable(
                name: "PodcastFeeds");
        }
    }
}
