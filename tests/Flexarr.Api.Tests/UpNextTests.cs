using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class UpNextTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task AFinishedSpecial_DoesNotTakeOverUpNext()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S02E01", 2, 1, null), ("S02E02", 2, 2, null), ("S00E01", 0, 1, null), ("S00E02", 0, 2, null));
        await WatchAsync(show.Episodes["S02E01"], DateTime.UtcNow.AddDays(-2));
        await WatchAsync(show.Episodes["S00E01"], DateTime.UtcNow.AddDays(-1));

        Assert.Equal("S02E02", await NextUpAsync(show.Series));
        Assert.Equal(["S02E02"], await ContinueWatchingAsync(show.Library));
    }

    [Fact]
    public async Task OnlySpecialsWatched_CarriesOnThroughTheSpecials()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S00E01", 0, 1, null), ("S00E02", 0, 2, null));
        await WatchAsync(show.Episodes["S00E01"], DateTime.UtcNow.AddDays(-1));

        Assert.Equal("S00E02", await NextUpAsync(show.Series));
    }

    [Fact]
    public async Task UpNext_MovesOnToTheNextSeason_AndSkipsWhatsBeenWatched()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S01E02", 1, 2, null), ("S02E01", 2, 1, null), ("S02E02", 2, 2, null));
        await WatchAsync(show.Episodes["S02E01"], DateTime.UtcNow.AddDays(-5));
        await WatchAsync(show.Episodes["S01E02"], DateTime.UtcNow.AddDays(-1));

        // After S01E02 comes S02E01, already seen: nothing is offered as Up Next (Play falls back to the first unwatched).
        Assert.Empty(await ContinueWatchingAsync(show.Library));
        Assert.Equal("S01E01", await NextUpAsync(show.Series));
    }

    [Fact]
    public async Task DailyEpisodes_SharingAnAirDate_AreBothUpNext()
    {
        var day = new DateOnly(2026, 3, 2);
        var show = await SeedAsync(("Monday A", 2026, null, day), ("Monday B", 2026, null, day), ("Tuesday", 2026, null, day.AddDays(1)));
        await WatchAsync(show.Episodes["Monday A"], DateTime.UtcNow.AddDays(-1));

        Assert.Equal("Monday B", await NextUpAsync(show.Series));
        await WatchAsync(show.Episodes["Monday B"], DateTime.UtcNow.AddHours(-1));
        Assert.Equal("Tuesday", await NextUpAsync(show.Series));
    }

    [Fact]
    public async Task TitlePage_GetsUpNextAndASeasonWithTheSeries()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S02E01", 2, 1, null), ("S02E02", 2, 2, null), ("S00E01", 0, 1, null));
        await WatchAsync(show.Episodes["S01E01"], DateTime.UtcNow.AddDays(-1));

        var page = await Client().GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{show.Series}?includeSeason=true", UriKind.Relative));
        Assert.Equal("S02E01", page.GetProperty("nextUp").GetProperty("title").GetString());
        // Season 1 is all watched, so the first unwatched one, with its episodes.
        var season = page.GetProperty("season");
        Assert.Equal(2, season.GetProperty("item").GetProperty("seasonNumber").GetInt32());
        Assert.Equal(["S02E01", "S02E02"], season.GetProperty("children").EnumerateArray().Select(e => e.GetProperty("title").GetString()!).ToArray());

        var specials = await Client().GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{show.Series}?includeSeason=true&season=0", UriKind.Relative));
        Assert.Equal(0, specials.GetProperty("season").GetProperty("item").GetProperty("seasonNumber").GetInt32());

        // Other callers (the Apple TV) get the detail as before.
        var plain = await Client().GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{show.Series}", UriKind.Relative));
        Assert.True(!plain.TryGetProperty("season", out var none) || none.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task MarkingAHalfWatchedEpisodeUnwatched_TakesTheShowOutOfContinueWatching()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S01E02", 1, 2, null), ("S01E03", 1, 3, null));
        await WatchAsync(show.Episodes["S01E01"], DateTime.UtcNow.AddDays(-2));
        await StartAsync(show.Episodes["S01E02"], DateTime.UtcNow.AddDays(-1));
        Assert.Equal(["S01E02"], await ContinueWatchingAsync(show.Library));

        (await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{show.Episodes["S01E02"]}/watched", UriKind.Relative), new { watched = false })).EnsureSuccessStatusCode();

        // Its place is forgotten, and the show doesn't come straight back offering the same episode as Up Next.
        Assert.Empty(await ContinueWatchingAsync(show.Library));
        using var scope = factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.FindAsync(PlaybackState.DefaultUser, show.Episodes["S01E02"]);
        Assert.Equal((0d, false), (state!.PositionSeconds, state.Watched));
    }

    [Fact]
    public async Task RemovingUpNext_HidesTheShowUntilAnotherEpisodeIsPlayed()
    {
        var show = await SeedAsync(("S01E01", 1, 1, null), ("S01E02", 1, 2, null), ("S01E03", 1, 3, null));
        await WatchAsync(show.Episodes["S01E01"], DateTime.UtcNow.AddDays(-2));
        Assert.Equal(["S01E02"], await ContinueWatchingAsync(show.Library));

        (await Client().DeleteAsync(new Uri($"/api/v1/home/continue/{show.Episodes["S01E02"]}", UriKind.Relative))).EnsureSuccessStatusCode();
        Assert.Empty(await ContinueWatchingAsync(show.Library));

        (await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{show.Episodes["S01E02"]}/watched", UriKind.Relative), new { watched = true })).EnsureSuccessStatusCode();
        Assert.Equal(["S01E03"], await ContinueWatchingAsync(show.Library));
    }

    [Fact]
    public async Task EveryPartPlayedFeaturedTitle_SaysWhatItResumes()
    {
        var lead = await SeedAsync(("S01E01", 1, 1, null), ("S01E02", 1, 2, null));
        var other = await SeedAsync(lead.Library, ("S01E01", 1, 1, null), ("S01E02", 1, 2, null));
        var fresh = await SeedAsync(lead.Library, ("S01E01", 1, 1, null));
        await WatchAsync(other.Episodes["S01E01"], DateTime.UtcNow.AddDays(-2));
        await StartAsync(lead.Episodes["S01E02"], DateTime.UtcNow.AddDays(-1));

        var browse = await Client().GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{lead.Library}/browse", UriKind.Relative));
        Assert.Equal(lead.Series.ToString(), browse.GetProperty("featured")[0].GetProperty("id").GetString());
        Assert.Equal(3, browse.GetProperty("featured").GetArrayLength());
        var resumes = browse.GetProperty("resumes");
        Assert.Equal(lead.Episodes["S01E02"].ToString(), resumes.GetProperty(lead.Series.ToString()).GetProperty("id").GetString());
        Assert.Equal(other.Episodes["S01E02"].ToString(), resumes.GetProperty(other.Series.ToString()).GetProperty("id").GetString());
        Assert.False(resumes.TryGetProperty(fresh.Series.ToString(), out _));
    }

    [Fact]
    public async Task RemovingAnUnknownTitle_IsNotFound()
    {
        var response = await Client().DeleteAsync(new Uri($"/api/v1/home/continue/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<string?> NextUpAsync(Guid series)
    {
        var response = await Client().GetAsync(new Uri($"/api/v1/items/{series}/next-up", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null
            : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
    }

    private async Task<string[]> ContinueWatchingAsync(Guid library)
    {
        var browse = await Client().GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{library}/browse", UriKind.Relative));
        return browse.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("id").GetString() == "continue")
            .SelectMany(r => r.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("title").GetString()!).ToArray();
    }

    private async Task WatchAsync(Guid episode, DateTime at)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episode, Watched = true, PlayCount = 1, LastPlayedAt = at });
        await db.SaveChangesAsync();
    }

    private async Task StartAsync(Guid episode, DateTime at)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episode, PositionSeconds = 600, DurationSeconds = 2400, LastPlayedAt = at });
        await db.SaveChangesAsync();
    }

    /// <summary>A show in a library of its own; episodes are (title, season, episode number, air date for daily shows).</summary>
    private Task<(Guid Library, Guid Series, Dictionary<string, Guid> Episodes)> SeedAsync(params (string Title, int Season, int? Number, DateOnly? AirDate)[] episodes) =>
        SeedAsync(null, episodes);

    /// <summary>As above, into <paramref name="libraryId"/> when given. The shows have what the hero needs to feature them.</summary>
    private async Task<(Guid Library, Guid Series, Dictionary<string, Guid> Episodes)> SeedAsync(Guid? libraryId, params (string Title, int Season, int? Number, DateOnly? AirDate)[] episodes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = libraryId is { } id ? await db.Libraries.FindAsync(id) : new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem
        {
            LibraryId = library!.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show", BackdropUrl = "backdrop.jpg", Overview = "A show.", CommunityRating = 8,
        };
        if (libraryId is null)
        {
            db.Add(library);
        }

        db.Add(series);
        var seasons = new Dictionary<int, MediaItem>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (title, number, (episode, airDate)) in episodes.Select(e => (e.Title, e.Season, (e.Number, e.AirDate))))
        {
            if (!seasons.TryGetValue(number, out var season))
            {
                season = seasons[number] = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = $"Season {number}", SortTitle = $"{number:D4}", SeasonNumber = number, Parent = series };
                db.Add(season);
            }

            // Daily episodes on one day share a number-based sort title in a real scan; the title tells them apart.
            var item = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Episode, Title = title, Parent = season, SeasonNumber = number, EpisodeNumber = episode, AirDate = airDate,
                SortTitle = episode is { } n ? $"{n:D4}" : $"{airDate!.Value.DayNumber:D4}-{title}",
            };
            db.Add(item);
            ids[title] = item.Id;
        }

        await db.SaveChangesAsync();
        return (library.Id, series.Id, ids);
    }
}
