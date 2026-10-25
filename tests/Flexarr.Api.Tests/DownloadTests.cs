using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AnMp4ThePhonePlays_IsReadyAtOnce_FetchedInPieces_AndKeptAfterReceipt()
    {
        var (movie, path) = await AddMovieAsync("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac");
        var client = Client();

        var options = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{movie}/download-options", UriKind.Relative));
        Assert.True(options.EnumerateArray().Single(o => o.GetProperty("quality").GetString() == "Original").GetProperty("available").GetBoolean());

        var created = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = movie, quality = "Original" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        Assert.Equal("Film", created.GetProperty("item").GetProperty("title").GetString());

        var ready = await WaitForAsync(client, id!, "Ready");
        Assert.Equal(new FileInfo(path).Length, ready.GetProperty("sizeBytes").GetInt64());

        // Resumable: a range comes back as just that range.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/offline/{id}/file", UriKind.Relative));
        request.Headers.Range = new RangeHeaderValue(10, 19);
        var part = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(10, (await part.Content.ReadAsByteArrayAsync()).Length);

        // Received: the server's copy goes, but this one is the library's own file, which stays.
        (await client.PostAsync(new Uri($"/api/v1/offline/{id}/received", UriKind.Relative), null)).EnsureSuccessStatusCode();
        Assert.True(File.Exists(path));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/offline/{id}/file", UriKind.Relative))).StatusCode);

        (await client.DeleteAsync(new Uri($"/api/v1/offline/{id}", UriKind.Relative))).EnsureSuccessStatusCode();
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>(new Uri("/api/v1/offline", UriKind.Relative)) ?? []);
    }

    [Fact]
    public async Task AnOriginalThePhoneCantPlay_IsNotOffered_AndFailsWithAReason()
    {
        var (movie, _) = await AddMovieAsync("matroska,webm", "hevc", "dts");
        var client = Client();

        var options = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{movie}/download-options", UriKind.Relative));
        Assert.False(options.EnumerateArray().Single(o => o.GetProperty("quality").GetString() == "Original").GetProperty("available").GetBoolean());

        var created = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = movie, quality = "Original" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var failed = await WaitForAsync(client, created.GetProperty("id").GetString()!, "Failed");
        Assert.Contains("Choose another size", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchingOffline_IsRecorded_UnlessSomethingNewerWas()
    {
        var (movie, _) = await AddMovieAsync("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac");
        var client = Client();
        var uri = new Uri($"/api/v1/items/{movie}/offline-progress", UriKind.Relative);

        (await client.PostAsJsonAsync(uri, new { positionSeconds = 300, finished = false, playedAt = DateTime.UtcNow.AddHours(-2) })).EnsureSuccessStatusCode();
        Assert.Equal(300, await PositionAsync(movie));

        // Older than what's recorded: ignored.
        (await client.PostAsJsonAsync(uri, new { positionSeconds = 100, finished = false, playedAt = DateTime.UtcNow.AddHours(-3) })).EnsureSuccessStatusCode();
        Assert.Equal(300, await PositionAsync(movie));

        (await client.PostAsJsonAsync(uri, new { positionSeconds = 0, finished = true, playedAt = DateTime.UtcNow.AddHours(-1) })).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.SingleAsync(p => p.MediaItemId == movie);
        Assert.True(state.Watched);
        Assert.Equal(1, state.PlayCount);
    }

    private async Task<double> PositionAsync(Guid item)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.SingleAsync(p => p.MediaItemId == item)).PositionSeconds;
    }

    private async Task<JsonElement> WaitForAsync(HttpClient client, string id, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            // The worker doesn't run in tests: a round of it, as its timer would.
            await _factory.Services.GetRequiredService<Flexarr.Api.Downloads.DownloadPreparer>().RunOnceAsync(CancellationToken.None);
            var download = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/offline/{id}", UriKind.Relative));
            if (download.GetProperty("status").GetString() == status)
            {
                return download;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Download {id} never became {status}");
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<(Guid Movie, string Path)> AddMovieAsync(string container, string video, string audio)
    {
        var path = Path.Combine(_factory.MediaDirectory, $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        var file = new MediaFile
        {
            MediaItemId = movie.Id, Path = path, Size = 4096, Container = container, DurationSeconds = 600,
            VideoCodec = video, Width = 1920, Height = 1080, AudioCodec = audio, AudioChannels = 2,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = video, Width = 1920, Height = 1080 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = audio, Channels = 2, Language = "eng", IsDefault = true },
            ],
        };
        db.AddRange(library, movie, file);
        await db.SaveChangesAsync();
        return (movie.Id, path);
    }
}
