using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task ThreeScreensOnOneProfile_AllPlay_WhileTheServerHasSlots()
    {
        var movie = await AddMovieAsync();
        var client = Client();

        // The default limit is 3.
        var screens = new[] { await StartAsync(client, movie), await StartAsync(client, movie), await StartAsync(client, movie) };

        foreach (var screen in screens)
        {
            Assert.True(await ExistsAsync(client, screen));
        }
    }

    [Fact]
    public async Task AtTheLimit_AProfilesOldestScreenMakesWay_IsToldWhy_AndItsRecoveryTakesNothing()
    {
        var movie = await AddMovieAsync();
        var owner = Client();
        var guest = Client(await NewProfileTokenAsync());

        var first = await StartAsync(owner, movie);
        var second = await StartAsync(owner, movie);
        var other = await StartAsync(guest, movie);
        var third = await StartAsync(owner, movie);

        var gone = await owner.GetAsync(new Uri($"/api/v1/playback/{first}/main.m3u8", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains("Someone started watching on another screen", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Gone, (await owner.GetAsync(new Uri($"/api/v1/playback/{first}/3.ts", UriKind.Relative))).StatusCode);

        // The ended screen's player tries to get its stream back: it's told why instead, and nobody else is ended.
        var recovery = await PostStartAsync(owner, movie, replaces: first);
        Assert.Equal(HttpStatusCode.Gone, recovery.StatusCode);
        foreach (var playing in new[] { second, other, third })
        {
            Assert.True(await ExistsAsync(owner, playing));
        }
    }

    [Fact]
    public async Task GettingTheNextEpisodeReady_NeverEndsAScreen_AndIsRefusedWhenTheServerIsFull()
    {
        var movie = await AddMovieAsync();
        var client = Client();

        // With room, it's an ordinary session.
        var prepared = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative),
            new { itemId = movie, profile = new { }, forceTranscode = true, prepare = true });
        prepared.EnsureSuccessStatusCode();
        var preparedId = (await prepared.Content.ReadFromJsonAsync<StartedPlayback>())!.SessionId;
        Assert.True(await ExistsAsync(client, preparedId));

        // Full: the screens playing carry on, and the next episode just isn't got ready.
        var screens = new[] { preparedId, await StartAsync(client, movie), await StartAsync(client, movie) };
        var refused = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative),
            new { itemId = movie, profile = new { }, forceTranscode = true, prepare = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        foreach (var screen in screens)
        {
            Assert.True(await ExistsAsync(client, screen));
        }
    }

    [Fact]
    public async Task RestartingAStreamTheServerForgot_EndsNobody_AndSaysTheServerIsBusy()
    {
        var movie = await AddMovieAsync();
        var client = Client();
        var screens = new[] { await StartAsync(client, movie), await StartAsync(client, movie), await StartAsync(client, movie) };

        var restart = await PostStartAsync(client, movie, replaces: "0123456789abcdef0123456789abcdef");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, restart.StatusCode);
        Assert.Contains("The stream stopped", await restart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (var screen in screens)
        {
            Assert.True(await ExistsAsync(client, screen));
        }
    }

    [Fact]
    public async Task SimultaneousStarts_CantTakeMoreThanAProfilesShare()
    {
        var movie = await AddMovieAsync();
        var guest = Client(await NewProfileTokenAsync());
        var other = await StartAsync(guest, movie);
        var owner = Client();

        var started = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => StartAsync(owner, movie)));

        Assert.True(await ExistsAsync(guest, other));
        var playing = 0;
        foreach (var screen in started)
        {
            playing += await ExistsAsync(owner, screen, goneIsFalse: true) ? 1 : 0;
        }

        Assert.Equal(2, playing);
    }

    private HttpClient Client(string? profileToken = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private async Task<string> NewProfileTokenAsync()
    {
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = $"Guest {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var selected = await Client().PostAsJsonAsync(new Uri($"/api/v1/profiles/{created.GetProperty("id").GetString()}/select", UriKind.Relative), new { });
        return (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
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

    private static Task<HttpResponseMessage> PostStartAsync(HttpClient client, Guid item, string? replaces = null) =>
        client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative),
            new { itemId = item, profile = new { }, forceTranscode = true, replacesSessionId = replaces });

    private static async Task<string> StartAsync(HttpClient client, Guid item, string? replaces = null)
    {
        var response = await PostStartAsync(client, item, replaces);
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<StartedPlayback>();
        Assert.Equal("Transcode", info!.Method);
        return info.SessionId;
    }

    private static async Task<bool> ExistsAsync(HttpClient client, string session, bool goneIsFalse = false)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/playback/{session}/main.m3u8", UriKind.Relative));
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            HttpStatusCode.Gone when goneIsFalse => false,
            var other => throw new InvalidOperationException($"Unexpected {other}"),
        };
    }

    private sealed record StartedPlayback(string SessionId, string Method);
}
