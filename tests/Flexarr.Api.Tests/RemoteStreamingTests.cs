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

    [Fact]
    public async Task AConversionForARemoteViewer_OpensWithOneSecondSegments_AndAtHomeTwo()
    {
        var movie = await AddMovieAsync();

        async Task<string> FirstSegmentAsync(string peer)
        {
            var client = Client(peer);
            var started = await StartAsync(client, movie, new { ForceTranscode = true });
            var playlist = await client.GetStringAsync(new Uri($"/api/v1/playback/{started.GetProperty("sessionId").GetString()}/main.m3u8", UriKind.Relative));
            return playlist.Split('\n').First(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal));
        }

        Assert.Equal("#EXTINF:1.000,", await FirstSegmentAsync(Away));
        Assert.Equal("#EXTINF:2.000,", await FirstSegmentAsync(Home));
    }

    private static async Task<List<string>> QualitiesAsync(HttpClient client, JsonElement started)
    {
        var master = await client.GetStringAsync(new Uri($"/api/v1/playback/{started.GetProperty("sessionId").GetString()}/master.m3u8", UriKind.Relative));
        var lines = master.Split('\n');
        return lines.Select((line, i) => (line, i)).Where(x => x.line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            .Select(x => System.Text.RegularExpressions.Regex.Match(x.line, "RESOLUTION=\\d+x(\\d+)").Groups[1].Value + " " + lines[x.i + 1]).ToList();
    }

    [Fact]
    public async Task WithNoReading_AFileOver4Mbps_GetsTheLadder_ServedUnderTheSameSession()
    {
        await UseEncoderAsync("VideoToolbox");
        var movie = await AddMovieAsync();
        var client = Client(Away);

        var started = await StartAsync(client, movie);

        Assert.Equal(["480 v/0/main.m3u8", "720 v/1/main.m3u8", "1080 v/2/main.m3u8"], await QualitiesAsync(client, started));
        var sid = started.GetProperty("sessionId").GetString();
        var low = await client.GetStringAsync(new Uri($"/api/v1/playback/{sid}/v/0/main.m3u8", UriKind.Relative));
        Assert.Contains("#EXTINF:1.000,\n0.ts", low, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/playback/{sid}/v/3/main.m3u8", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/playback/{sid}/v/1/99999.ts", UriKind.Relative))).StatusCode);
        // The single-quality routes still answer, with the top quality.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"/api/v1/playback/{sid}/main.m3u8", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ASpeedThatFitsTheFile_GetsTheOriginal_ASlowOneTheLadder_AndOriginalAlwaysTheFile()
    {
        await UseEncoderAsync("VideoToolbox");
        var movie = await AddMovieAsync();
        var client = Client(Away);

        Assert.Equal("DirectPlay", (await StartAsync(client, movie, new { MeasuredBitrate = 100_000_000L })).GetProperty("method").GetString());
        var slow = await StartAsync(client, movie, new { MeasuredBitrate = 6_000_000L });
        Assert.Equal(3, (await QualitiesAsync(client, slow)).Count);
        Assert.Equal("DirectPlay", (await StartAsync(client, movie, new { MeasuredBitrate = 2_000_000L, Original = true })).GetProperty("method").GetString());

        // A quality asked for (the menu's 720p): that one stream, no ladder.
        var asked = await StartAsync(client, movie, new { MaxHeight = 720, MaxBitrate = 4_000_000L });
        Assert.Equal(["720 main.m3u8"], await QualitiesAsync(client, asked));
    }

    [Theory]
    [InlineData(720, new[] { 480, 720 })]
    [InlineData(480, new[] { 480 })]
    public async Task OnlyQualitiesAtOrBelowTheFilesOwn_AndFewerThanTwoIsOneStream(int height, int[] expected)
    {
        await UseEncoderAsync("VideoToolbox");
        var movie = await AddMovieAsync(height: height, bitrate: 12_000_000);
        var client = Client(Away);

        var started = await StartAsync(client, movie);

        Assert.Equal(expected, (await QualitiesAsync(client, started)).Select(q => int.Parse(q.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ConvertingInSoftware_IsOneStream_HeldTo720p()
    {
        await UseEncoderAsync("None");
        var movie = await AddMovieAsync();
        var client = Client(Away);

        Assert.Equal(["720 main.m3u8"], await QualitiesAsync(client, await StartAsync(client, movie)));
    }

    [Fact]
    public async Task WithTheServerFull_ItsOneStream_NotTheLadder()
    {
        await UseEncoderAsync("VideoToolbox");
        var movie = await AddMovieAsync();
        var home = Client(Home);
        for (var i = 0; i < 3; i++)
        {
            await StartAsync(home, movie, new { ForceTranscode = true });
        }

        var away = Client(Away);
        var started = await StartAsync(away, movie);

        Assert.Equal(["720 main.m3u8"], await QualitiesAsync(away, started));
    }

    [Fact]
    public async Task TheLadder_SaysItsAdaptive_AndActivityShowsTheQualityLastFetched()
    {
        await UseEncoderAsync("VideoToolbox");
        var movie = await AddMovieAsync();
        var client = Client(Away);

        var started = await StartAsync(client, movie);

        Assert.True(started.GetProperty("adaptive").GetBoolean());
        Assert.Equal(20_000_000, started.GetProperty("fileBitrate").GetInt64());
        Assert.Equal(480, started.GetProperty("stream").GetProperty("height").GetInt32());

        // The player moves to 720p (there's no real film here, so the segment itself fails).
        var sid = started.GetProperty("sessionId").GetString();
        await client.GetAsync(new Uri($"/api/v1/playback/{sid}/v/1/0.ts", UriKind.Relative));
        var activity = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/system/activity", UriKind.Relative));
        var stream = activity.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("sessionId").GetString() == sid);
        Assert.StartsWith("720p at 3.7 Mbps", stream.GetProperty("transcode").GetString(), StringComparison.Ordinal);

        // One stream isn't adaptive.
        var single = await StartAsync(Client(Away), movie, new { MaxHeight = 720, MaxBitrate = 4_000_000L });
        Assert.False(single.GetProperty("adaptive").GetBoolean());
    }
}
