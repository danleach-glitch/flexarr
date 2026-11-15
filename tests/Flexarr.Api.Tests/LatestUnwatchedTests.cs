using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class LatestUnwatchedTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task Shows_AreOrderedByTheirNewestUnwatchedEpisode_SoAnUpgradeOfOneSeenDoesNotBringAShowForward()
    {
        Guid library, upgraded, fresh, seen;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var shows = new Library { Name = "Shows", Type = LibraryType.TvShows };
            db.Libraries.Add(shows);
            var now = DateTime.UtcNow;

            // A long-running show: one episode left from years ago, and one already seen that Sonarr upgraded yesterday.
            var (upgradedShow, upgradedEpisodes) = Show(db, shows.Id, "Upgraded", now.AddYears(-5), now.AddDays(-1));
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = upgradedEpisodes[1].Id, Watched = true, PlayCount = 1 });
            // A show with an episode that's new this week.
            var (freshShow, _) = Show(db, shows.Id, "Fresh", now.AddDays(-3));
            // A show that's all seen: not in the row at all, however new.
            var (seenShow, seenEpisodes) = Show(db, shows.Id, "Seen", now);
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = seenEpisodes[0].Id, Watched = true, PlayCount = 1 });
            await db.SaveChangesAsync();
            (library, upgraded, fresh, seen) = (shows.Id, upgradedShow.Id, freshShow.Id, seenShow.Id);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var home = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{library}/browse", UriKind.Relative));
        var row = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "recent");

        Assert.Equal([fresh, upgraded], row.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(seen.ToString(), row.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyShowsWithTheirNewestEpisodesSeen_StillGiveTheSameRowAsCheckingEveryEpisode()
    {
        Guid library;
        var expected = new List<(Guid Id, DateTime At)>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var shows = new Library { Name = "Lots of shows", Type = LibraryType.TvShows };
            db.Libraries.Add(shows);
            var now = DateTime.UtcNow;
            // 130 shows (more than a couple of batches looked at together): the newest episode of most is seen, and
            // every other one has an older episode left.
            for (var k = 0; k < 130; k++)
            {
                var newest = now.AddHours(-k);
                var older = now.AddHours(-(200 + (k * 7 % 130)));
                var (show, episodes) = Show(db, shows.Id, $"Show {k:000}", older, newest);
                if (k < 90)
                {
                    db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episodes[1].Id, Watched = true, PlayCount = 1 });
                    if (k % 2 == 0)
                    {
                        expected.Add((show.Id, older));
                    }
                    else
                    {
                        db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episodes[0].Id, Watched = true, PlayCount = 1 });
                    }
                }
                else
                {
                    expected.Add((show.Id, newest));
                }
            }

            await db.SaveChangesAsync();
            library = shows.Id;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var home = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{library}/browse", UriKind.Relative));
        var row = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "recent");

        Assert.Equal(expected.OrderByDescending(e => e.At).Take(20).Select(e => e.Id), row.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    private static (MediaItem Show, List<MediaItem> Episodes) Show(FlexarrDbContext db, Guid library, string title, params DateTime[] added)
    {
        var show = new MediaItem { LibraryId = library, Kind = MediaKind.Series, Title = title, SortTitle = title.ToLowerInvariant(), AddedAt = added.Min() };
        var season = new MediaItem { LibraryId = library, ParentId = show.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "0001", SeasonNumber = 1, AddedAt = added.Min() };
        var episodes = added.Select((at, i) => new MediaItem
        {
            LibraryId = library, ParentId = season.Id, Kind = MediaKind.Episode, Title = $"Episode {i + 1}", SortTitle = $"{i + 1:0000}",
            SeasonNumber = 1, EpisodeNumber = i + 1, AddedAt = at,
        }).ToList();
        db.MediaItems.AddRange([show, season, .. episodes]);
        return (show, episodes);
    }
}
