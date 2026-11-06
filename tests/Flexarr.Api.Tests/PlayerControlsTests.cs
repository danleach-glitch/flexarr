using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// The player's audio sync and speed: a file's sound offset is the household's and is the file's alone, and a show's
/// speed is kept for each profile's next episodes.
/// </summary>
public class PlayerControlsTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Fact]
    public async Task AnEpisodesSoundOffset_IsItsOwn_AndPlaybackCarriesIt()
    {
        var (_, episodes) = await AddShowAsync(3);
        var client = Client();

        // Fixed on the second episode: the others are left as they are.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[1]}/audio-delay"), new { seconds = 0.2 })).StatusCode);
        Assert.Equal(new[] { 0, 0.2, 0 }, await DelaysAsync(episodes));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[2]}/audio-delay"), new { seconds = -0.35 })).StatusCode);
        Assert.Equal(new[] { 0, 0.2, -0.35 }, await DelaysAsync(episodes));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/audio-delay"), new { seconds = 2.5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Api($"/items/{Guid.NewGuid()}/audio-delay"), new { seconds = 0.1 })).StatusCode);

        // A player that can play sound earlier gets the file as it is and moves the sound itself; one that can't gets
        // the server's stream with the sound already moved.
        var engine = await StartAsync(client, episodes[2], new { advancesAudio = true, delaysAudio = true, selectsTracksItself = true, containers = new[] { "matroska,webm" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } });
        Assert.Equal("DirectPlay", engine.GetProperty("method").GetString());
        Assert.Equal(-0.35, engine.GetProperty("audioDelaySeconds").GetDouble());
        Assert.False(engine.GetProperty("audioDelayInStream").GetBoolean());

        var browser = await StartAsync(client, episodes[2], new { delaysAudio = true, containers = new[] { "matroska,webm" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } });
        Assert.Equal("Transcode", browser.GetProperty("method").GetString());
        Assert.True(browser.GetProperty("audioDelayInStream").GetBoolean());
        Assert.Contains("The sound is moved earlier to match the picture", browser.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task AShowsSpeed_IsKeptForItsOtherEpisodes_AndOneForgetsIt()
    {
        var (_, episodes) = await AddShowAsync(2);
        var client = Client();

        var before = await StartAsync(client, episodes[1], new { });
        Assert.Equal(JsonValueKind.Null, before.GetProperty("speed").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/playback-speed"), new { speed = 1.25 })).StatusCode);
        Assert.Equal(1.25, (await StartAsync(client, episodes[1], new { })).GetProperty("speed").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/playback-speed"), new { speed = 1 })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await StartAsync(client, episodes[1], new { })).GetProperty("speed").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/playback-speed"), new { speed = 8 })).StatusCode);
    }

    [Fact]
    public async Task AStreamBeingConverted_IsMadeAsFastAsItsWatched()
    {
        var (_, episodes) = await AddShowAsync(2);
        var client = Client();
        var sessions = factory.Services.GetRequiredService<Flexarr.Api.Playback.PlaybackSessions>();
        async Task<Flexarr.Streaming.TranscodeSession> ConvertAsync(Guid episode)
        {
            var response = await client.PostAsJsonAsync(Api("/playback"), new { itemId = episode, profile = new { }, forceTranscode = true });
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
            return sessions.Get(id)!.Transcode!;
        }

        // No speed kept for the show: real time.
        var first = await ConvertAsync(episodes[0]);
        Assert.Equal(1, first.Speed);

        // Sped up while it plays: the stream being made for it follows, and the next episode starts that fast.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/playback-speed"), new { speed = 1.5 })).StatusCode);
        Assert.Equal(1.5, first.Speed);
        var second = await ConvertAsync(episodes[1]);
        Assert.Equal(1.5, second.Speed);
        Assert.Equal(1.5, first.Speed);

        // A player that reports its speed (the Apple apps) is followed too; one that doesn't say changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Api($"/playback/{second.Id}/progress"), new { positionSeconds = 30, rate = 2.0 })).StatusCode);
        Assert.Equal(2, second.Speed);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Api($"/playback/{second.Id}/progress"), new { positionSeconds = 40 })).StatusCode);
        Assert.Equal(2, second.Speed);
        // Slower than normal is still made at real time.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Api($"/playback/{second.Id}/progress"), new { positionSeconds = 50, rate = 0.5 })).StatusCode);
        Assert.Equal(1, second.Speed);

        await client.DeleteAsync(Api($"/playback/{first.Id}"));
        await client.DeleteAsync(Api($"/playback/{second.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Api($"/items/{episodes[0]}/playback-speed"), new { speed = 1 })).StatusCode);
    }

    private async Task<double[]> DelaysAsync(List<Guid> episodes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var delays = await db.MediaFiles.Where(f => episodes.Contains(f.MediaItemId)).ToDictionaryAsync(f => f.MediaItemId, f => f.AudioDelaySeconds);
        return [.. episodes.Select(e => delays[e])];
    }

    private async Task<(Guid Series, List<Guid> Episodes)> AddShowAsync(int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Shows {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show" };
        var season = new MediaItem { LibraryId = library.Id, ParentId = series.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "season 1", SeasonNumber = 1 };
        db.AddRange(library, series, season);
        var episodes = new List<Guid>();
        for (var n = 1; n <= count; n++)
        {
            var episode = new MediaItem
            {
                LibraryId = library.Id, ParentId = season.Id, Kind = MediaKind.Episode, Title = $"Episode {n}", SortTitle = $"episode {n}",
                SeasonNumber = 1, EpisodeNumber = n,
            };
            db.AddRange(episode, new MediaFile
            {
                MediaItemId = episode.Id,
                Path = Path.Combine(factory.MediaDirectory, $"{episode.Id:N}.mkv"),
                Container = "matroska,webm",
                DurationSeconds = 600,
                VideoCodec = "h264",
                Width = 1920,
                Height = 1080,
                AudioCodec = "aac",
                AudioChannels = 2,
                Streams =
                [
                    new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
                    new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
                ],
            });
            episodes.Add(episode.Id);
        }

        await db.SaveChangesAsync();
        return (series.Id, episodes);
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, Guid item, object profile)
    {
        var response = await client.PostAsJsonAsync(Api("/playback"), new { itemId = item, profile });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        await client.DeleteAsync(Api($"/playback/{info.GetProperty("sessionId").GetString()}"));
        return info;
    }
}
