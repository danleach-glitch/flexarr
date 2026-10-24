using System.Net;
using System.Net.Http.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class PlaybackSessionTests : IDisposable
{
    // Its own server: the transcode limit counts every session the server has.
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TwoScreensOnOneProfile_CanWatchTheSameTitle_AndAPlayerReplacesOnlyItsOwnSession()
    {
        var movie = await AddMovieAsync();
        var client = Client();

        var living = await StartAsync(client, movie);
        var bedroom = await StartAsync(client, movie);
        Assert.True(await ExistsAsync(client, living));
        Assert.True(await ExistsAsync(client, bedroom));

        // The bedroom switches audio track: its new session takes over from its old one, and only that one.
        var switched = await StartAsync(client, movie, replaces: bedroom);
        Assert.False(await ExistsAsync(client, bedroom));
        Assert.True(await ExistsAsync(client, living));
        Assert.True(await ExistsAsync(client, switched));
    }

    [Fact]
    public async Task OneProfile_CantTakeEveryTranscodeSlot_ItsOldestMakesWay()
    {
        var movie = await AddMovieAsync();
        var client = Client();

        // The default limit is 3, so one profile gets 2.
        var first = await StartAsync(client, movie);
        var second = await StartAsync(client, movie);
        var third = await StartAsync(client, movie);

        Assert.False(await ExistsAsync(client, first));
        Assert.True(await ExistsAsync(client, second));
        Assert.True(await ExistsAsync(client, third));
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<Guid> AddMovieAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        var file = new MediaFile
        {
            MediaItemId = movie.Id,
            Path = Path.Combine(_factory.MediaDirectory, "film.mkv"),
            Container = "matroska,webm",
            DurationSeconds = 600,
            VideoCodec = "hevc",
            Width = 1920,
            Height = 1080,
            AudioCodec = "aac",
            AudioChannels = 2,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Width = 1920, Height = 1080 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
            ],
        };
        db.AddRange(library, movie, file);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    private static async Task<string> StartAsync(HttpClient client, Guid item, string? replaces = null)
    {
        var response = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative),
            new { itemId = item, profile = new { }, forceTranscode = true, replacesSessionId = replaces });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<StartedPlayback>();
        Assert.Equal("Transcode", info!.Method);
        return info.SessionId;
    }

    private static async Task<bool> ExistsAsync(HttpClient client, string session)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/playback/{session}/main.m3u8", UriKind.Relative));
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            var other => throw new InvalidOperationException($"Unexpected {other}"),
        };
    }

    private sealed record StartedPlayback(string SessionId, string Method);
}
