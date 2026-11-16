using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>What a start away from home gets (docs/remote-streaming.md): the original, the ladder, or one capped stream.</summary>
public sealed class RemoteStreamingTests : IAsyncLifetime
{
    private const string Away = "81.2.69.160";
    private const string Home = "192.168.1.20";

    // Its own server: the transcode limit counts every session the server has.
    private readonly FlexarrApiFactory _factory = new();
    private WebApplicationFactory<Program> _server = null!;

    /// <summary>Plays HEVC in MKV as it is, so only the connection can make the server convert.</summary>
    private static readonly object Player = new { containers = new[] { "matroska,webm", "mp4" }, videoCodecs = new[] { "h264", "hevc" }, audioCodecs = new[] { "aac" } };

    public Task InitializeAsync() => UseEncoderAsync("None");

    private async Task UseEncoderAsync(string encoder)
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _server = _factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:Transcoding:HardwareAcceleration", encoder));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private HttpClient Client(string peer)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        return client;
    }

    private async Task<Guid> AddMovieAsync(int height = 1080, long bitrate = 20_000_000)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        var file = new MediaFile
        {
            MediaItemId = movie.Id,
            Path = Path.Combine(_factory.MediaDirectory, $"film-{Guid.NewGuid():N}.mkv"),
            Container = "matroska,webm",
            DurationSeconds = 600,
            VideoCodec = "hevc",
            Width = height * 16 / 9,
            Height = height,
            Bitrate = bitrate,
            AudioCodec = "aac",
            AudioChannels = 2,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Width = height * 16 / 9, Height = height },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
            ],
        };
        db.AddRange(library, movie, file);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, Guid item, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["itemId"] = item, ["profile"] = Player };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(extra);
            }
        }

        var response = await client.PostAsJsonAsync(new Uri("/api/v1/playback", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task WithNoReadingAndNoLadder_ARemoteStartIsHeldTo720p4Mbps_AndSaysItsTheConnection()
    {
        var movie = await AddMovieAsync();

        var away = await StartAsync(Client(Away), movie);

        Assert.Equal("Transcode", away.GetProperty("method").GetString());
        Assert.Equal(["Your connection is slower than this file needs"], away.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        var stream = away.GetProperty("stream");
        Assert.Equal(720, stream.GetProperty("height").GetInt32());
        Assert.True(stream.GetProperty("videoBitrate").GetInt64() < 4_000_000);

        // At home the same file plays as it is.
        Assert.Equal("DirectPlay", (await StartAsync(Client(Home), movie)).GetProperty("method").GetString());
    }

    [Fact]
    public async Task WithNoReading_ASmallFilePlaysAsItIsAwayFromHome()
    {
        var movie = await AddMovieAsync(bitrate: 3_000_000);

        Assert.Equal("DirectPlay", (await StartAsync(Client(Away), movie)).GetProperty("method").GetString());
    }
}
