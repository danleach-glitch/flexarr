using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class ShuffleTests : IDisposable
{
    // Its own server: every start here is a session, and the server limits them.
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ShufflingAShow_StartsOneOfItsEpisodesFromTheBeginning_AndNextIsAnotherOfThem()
    {
        var show = await SeedAsync(regular: 6, specials: 2, unreadable: 1);
        var client = Client();

        for (var i = 0; i < 12; i++)
        {
            var info = await StartAsync(client, new { itemId = show.Series, profile = Mp4, shuffle = true, startPositionSeconds = (double?)null });
            var playing = Guid.Parse(info.GetProperty("item").GetProperty("id").GetString()!);
            Assert.Contains(playing, show.Regular);
            Assert.Equal(0, info.GetProperty("startPositionSeconds").GetDouble());
            Assert.True(info.GetProperty("shuffle").GetBoolean());

            var next = Guid.Parse(info.GetProperty("next").GetProperty("id").GetString()!);
            Assert.Contains(next, show.Regular);
            Assert.NotEqual(playing, next);
            await StopAsync(client, info);
        }
    }

    [Fact]
    public async Task WithoutShuffle_AnEpisodesNextIsTheFollowingOne_AndAShowCantBePlayed()
    {
        var show = await SeedAsync(regular: 3, specials: 0, unreadable: 0);
        var client = Client();

        var info = await StartAsync(client, new { itemId = show.Regular[0], profile = Mp4 });
        Assert.Equal(show.Regular[1], Guid.Parse(info.GetProperty("next").GetProperty("id").GetString()!));
        Assert.False(info.GetProperty("shuffle").GetBoolean());
        await StopAsync(client, info);

        var series = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative), new { itemId = show.Series, profile = Mp4 });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, series.StatusCode);
    }

    [Fact]
    public void ThePick_LeavesOutTheCurrentEpisode_SpecialsAndOnesFinishedToday()
    {
        var now = DateTime.UtcNow;
        var current = Episode(1, 1, null);
        var today = Episode(1, 2, now.AddHours(-2));
        var lastWeek = Episode(1, 3, now.AddDays(-7));
        var never = Episode(2, 1, null);
        var special = Episode(0, 1, null);
        var episodes = new[] { current, today, lastWeek, never, special };

        var picks = Enumerable.Range(0, 200).Select(seed => PlaybackService.PickShuffled(episodes, current.Id, new Random(seed), now)!.Id).ToHashSet();
        Assert.Equal(new HashSet<Guid> { lastWeek.Id, never.Id }, picks);

        // Everything else finished today: those are fair game rather than nothing.
        Assert.Equal(today.Id, PlaybackService.PickShuffled([current, today], current.Id, new Random(1), now)!.Id);
        // A show of specials alone shuffles its specials.
        Assert.Equal(special.Id, PlaybackService.PickShuffled([special], null, new Random(1), now)!.Id);
        Assert.Null(PlaybackService.PickShuffled([current], current.Id, new Random(1), now));
    }

    private static PlaybackService.EpisodeRef Episode(int season, int number, DateTime? watchedAt) =>
        new(Guid.NewGuid(), Guid.Empty, Guid.Empty, season, number, null, null, $"{number:D4}", watchedAt);

    /// <summary>A device that plays these MP4s as they are, so nothing is converted.</summary>
    private static readonly object Mp4 = new
    {
        client = "test", containers = new[] { "mp4" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" },
        hdr = false, maxHeight = 2160, maxAudioChannels = 6, maxBitrate = 0,
    };

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative), request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> StopAsync(HttpClient client, JsonElement info) =>
        client.DeleteAsync(new Uri($"/api/v1/playback/{info.GetProperty("sessionId").GetString()}", UriKind.Relative));

    /// <summary>
    /// A show with <paramref name="regular"/> playable episodes in seasons 1 and 2, <paramref name="specials"/> in
    /// season 0, and <paramref name="unreadable"/> whose file ffprobe couldn't read.
    /// </summary>
    private async Task<(Guid Series, List<Guid> Regular)> SeedAsync(int regular, int specials, int unreadable)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show" };
        db.AddRange(library, series);
        var seasons = new Dictionary<int, MediaItem>();
        var ids = new List<Guid>();
        var number = 0;

        void Add(int seasonNumber, bool readable)
        {
            if (!seasons.TryGetValue(seasonNumber, out var season))
            {
                season = seasons[seasonNumber] = new MediaItem
                {
                    LibraryId = library.Id, Kind = MediaKind.Season, Title = $"Season {seasonNumber}", SortTitle = $"{seasonNumber:D4}", SeasonNumber = seasonNumber, Parent = series,
                };
                db.Add(season);
            }

            number++;
            var episode = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Episode, Title = $"Episode {number}", SortTitle = $"{number:D4}", Parent = season, SeasonNumber = seasonNumber, EpisodeNumber = number,
            };
            db.Add(episode);
            db.Add(new MediaFile
            {
                MediaItemId = episode.Id, Path = Path.Combine(_factory.MediaDirectory, $"{Guid.NewGuid():N}.mp4"), Container = "mov,mp4,m4a,3gp,3g2,mj2",
                DurationSeconds = 1500, VideoCodec = "h264", Width = 1920, Height = 1080, AudioCodec = "aac", AudioChannels = 2,
                ProbeError = readable ? null : "Couldn't read this file",
                Streams =
                [
                    new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
                    new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
                ],
            });
            if (readable && seasonNumber != 0)
            {
                ids.Add(episode.Id);
            }
        }

        for (var i = 0; i < regular; i++)
        {
            Add(i < regular / 2 ? 1 : 2, readable: true);
        }

        for (var i = 0; i < specials; i++)
        {
            Add(0, readable: true);
        }

        for (var i = 0; i < unreadable; i++)
        {
            Add(1, readable: false);
        }

        await db.SaveChangesAsync();
        return (series.Id, ids);
    }
}

public sealed class PlaybackPreferencesTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AProfile_StartsWithAutoPlayOnAndNothingSkipped_AndCanChangeItsOwn()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var uri = new Uri("/api/v1/me/playback-preferences", UriKind.Relative);

        var first = await client.GetFromJsonAsync<JsonElement>(uri);
        Assert.True(first.GetProperty("autoPlayNext").GetBoolean());
        Assert.False(first.GetProperty("skipIntros").GetBoolean());
        Assert.False(first.GetProperty("skipCredits").GetBoolean());

        (await client.PutAsJsonAsync(uri, new { autoPlayNext = false, skipIntros = true, skipRecaps = true, skipCredits = true })).EnsureSuccessStatusCode();
        var saved = await client.GetFromJsonAsync<JsonElement>(uri);
        Assert.False(saved.GetProperty("autoPlayNext").GetBoolean());
        Assert.True(saved.GetProperty("skipIntros").GetBoolean());
        Assert.True(saved.GetProperty("skipRecaps").GetBoolean());
        Assert.True(saved.GetProperty("skipCredits").GetBoolean());
    }

    [Fact]
    public async Task SubtitleSize_IsStandardToStart_AndKeptWhenAnOlderAppSavesItsToggles()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var uri = new Uri("/api/v1/me/playback-preferences", UriKind.Relative);
        Assert.Equal("Standard", (await client.GetFromJsonAsync<JsonElement>(uri)).GetProperty("subtitleSize").GetString());

        (await client.PutAsJsonAsync(uri, new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, subtitleSize = "Large" })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(uri, new { autoPlayNext = false, skipIntros = false, skipRecaps = false, skipCredits = false })).EnsureSuccessStatusCode();

        var saved = await client.GetFromJsonAsync<JsonElement>(uri);
        Assert.Equal("Large", saved.GetProperty("subtitleSize").GetString());
        Assert.False(saved.GetProperty("autoPlayNext").GetBoolean());
    }

    [Fact]
    public async Task SubtitlePlacement_StartsAtTheBottomOverThePicture_AndIsKeptWhenAnOlderAppSavesItsToggles()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var uri = new Uri("/api/v1/me/playback-preferences", UriKind.Relative);
        var first = await client.GetFromJsonAsync<JsonElement>(uri);
        Assert.Equal(("Bottom", false), (first.GetProperty("subtitlePosition").GetString(), first.GetProperty("subtitlesInBlackBars").GetBoolean()));

        (await client.PutAsJsonAsync(uri, new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, subtitlePosition = "Higher", subtitlesInBlackBars = true })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(uri, new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, subtitleSize = "Small" })).EnsureSuccessStatusCode();

        var saved = await client.GetFromJsonAsync<JsonElement>(uri);
        Assert.Equal(("Higher", true, "Small"), (saved.GetProperty("subtitlePosition").GetString(), saved.GetProperty("subtitlesInBlackBars").GetBoolean(), saved.GetProperty("subtitleSize").GetString()));
    }

    [Fact]
    public async Task SubtitleDelay_IsRememberedForTheTitle_WithinAMinute()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var movie = new MediaItem { LibraryId = Guid.NewGuid(), Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.AddRange(new Library { Id = movie.LibraryId, Name = "Films", Type = LibraryType.Movies }, movie);
            await db.SaveChangesAsync();
        }

        var uri = new Uri($"/api/v1/items/{movie.Id}/subtitle-delay", UriKind.Relative);
        (await client.PutAsJsonAsync(uri, new { seconds = -1.5 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(uri, new { seconds = 90 })).StatusCode);

        using var check = _factory.Services.CreateScope();
        var state = await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.SingleAsync(p => p.MediaItemId == movie.Id);
        Assert.Equal(-1.5, state.SubtitleDelaySeconds);
    }
}
