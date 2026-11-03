using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Candidate = Flexarr.Api.Endpoints.DiscoveryEndpoints.FeaturedCandidate;

namespace Flexarr.Api.Tests;

/// <summary>The hero's featured titles: a new handful each day, the same on every device, and not what's been seen.</summary>
public class FeaturedTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly int Today = new DateOnly(2026, 11, 3).DayNumber;

    private static List<Candidate> Pool(int count) =>
        Enumerable.Range(0, count).Select(n => new Candidate(new Guid(n, 0, 0, new byte[8]), Seen: false, HasLogo: true)).ToList();

    [Fact]
    public void ThePicks_ChangeFromDayToDay_AndReachTheWholePool()
    {
        var pool = Pool(600);
        var days = Enumerable.Range(0, 30).Select(d => DiscoveryEndpoints.PickFeatured(pool, "owner", Today + d)).ToList();

        // Each day's five are mostly new (an old XOR-the-day shuffle gave the same five all year).
        for (var d = 1; d < days.Count; d++)
        {
            Assert.True(days[d].Intersect(days[d - 1]).Count() <= 2, $"day {d} repeats day {d - 1}");
        }

        // A month reaches well past the first sixty titles the server used to read.
        var seen = days.SelectMany(d => d).Distinct().ToList();
        Assert.True(seen.Count > 100);
        Assert.Contains(seen, id => pool.FindIndex(c => c.Id == id) >= 60);
    }

    [Fact]
    public void ThePicks_AreTheSameAllDay_ForAProfile()
    {
        var pool = Pool(200);
        Assert.Equal(DiscoveryEndpoints.PickFeatured(pool, "owner", Today), DiscoveryEndpoints.PickFeatured(pool.AsEnumerable().Reverse(), "owner", Today));
        Assert.NotEqual(DiscoveryEndpoints.PickFeatured(pool, "owner", Today), DiscoveryEndpoints.PickFeatured(pool, "someone-else", Today));
    }

    [Fact]
    public void SeenTitles_OnlyFillIn()
    {
        var unseen = Pool(3);
        var seen = Enumerable.Range(100, 50).Select(n => new Candidate(new Guid(n, 0, 0, new byte[8]), Seen: true, HasLogo: true)).ToList();
        var picks = DiscoveryEndpoints.PickFeatured([.. seen, .. unseen], "owner", Today);

        Assert.Equal(5, picks.Count);
        Assert.Equal(unseen.Select(c => c.Id).Order(), picks.Take(3).Order());
    }

    [Fact]
    public async Task TheHero_LeavesOutFinishedShowsAndWatchedFilms()
    {
        Guid library, finished, started, untouched, watchedFilm, freshFilm;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var lib = new Library { Name = $"Mixed {Guid.NewGuid():N}", Type = LibraryType.TvShows };
            db.Add(lib);
            library = lib.Id;

            MediaItem Show(string title, int watchedEpisodes)
            {
                var series = Title(MediaKind.Series, title);
                var season = new MediaItem { LibraryId = library, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "0001", SeasonNumber = 1, Parent = series };
                db.AddRange(series, season);
                for (var n = 1; n <= 3; n++)
                {
                    var episode = new MediaItem { LibraryId = library, Kind = MediaKind.Episode, Title = $"{title} {n}", SortTitle = $"{n:D4}", SeasonNumber = 1, EpisodeNumber = n, Parent = season };
                    db.Add(episode);
                    if (n <= watchedEpisodes)
                    {
                        db.PlaybackStates.Add(new PlaybackState { MediaItemId = episode.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddYears(-1) });
                    }
                }

                return series;
            }

            MediaItem Title(MediaKind kind, string title) => new()
            {
                LibraryId = library, Kind = kind, Title = title, SortTitle = title.ToLowerInvariant(), BackdropUrl = "backdrop.jpg", LogoUrl = "logo.png", Overview = "Something.", CommunityRating = 8,
            };

            finished = Show("Finished", 3).Id;
            started = Show("Started", 1).Id;
            untouched = Show("Untouched", 0).Id;
            var film = Title(MediaKind.Movie, "Seen film");
            var fresh = Title(MediaKind.Movie, "Fresh film");
            db.AddRange(film, fresh);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddYears(-1) });
            watchedFilm = film.Id;
            freshFilm = fresh.Id;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var browse = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{library}/browse", UriKind.Relative));
        var featured = browse.GetProperty("featured").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        // Five places, three unseen titles: those first, then the seen ones fill in.
        Assert.Equal(new[] { started, untouched, freshFilm }.Order(), featured.Take(3).Order());
        Assert.Equal(new[] { finished, watchedFilm }.Order(), featured.Skip(3).Order());
    }
}
