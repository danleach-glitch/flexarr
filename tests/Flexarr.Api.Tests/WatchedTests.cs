using System.Net.Http.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class WatchedTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task MarkingAShowWatched_LeavesEpisodesAlreadyWatchedAsTheyWere()
    {
        var watchedAt = new DateTime(2024, 3, 1, 20, 0, 0, DateTimeKind.Utc);
        Guid show, seen, unseen;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
            var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show" };
            var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", Parent = series };
            var e1 = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "One", SortTitle = "1", Parent = season };
            var e2 = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Two", SortTitle = "2", Parent = season };
            db.AddRange(library, series, season, e1, e2);
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = e1.Id, Watched = true, PlayCount = 3, LastPlayedAt = watchedAt });
            await db.SaveChangesAsync();
            (show, seen, unseen) = (series.Id, e1.Id, e2.Id);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        (await client.PostAsJsonAsync(new Uri($"/api/v1/items/{show}/watched", UriKind.Relative), new { watched = true })).EnsureSuccessStatusCode();

        using var check = factory.Services.CreateScope();
        var states = await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.AsNoTracking()
            .Where(p => p.MediaItemId == seen || p.MediaItemId == unseen).ToDictionaryAsync(p => p.MediaItemId);
        Assert.Equal((watchedAt, 3), (states[seen].LastPlayedAt, states[seen].PlayCount));
        Assert.True(states[unseen].Watched);
    }
}
