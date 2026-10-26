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
    public async Task ASecondDevice_AskingForWhatTheFirstReceived_GetsItsOwnCopy()
    {
        var (movie, _) = await AddMovieAsync("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac");
        var client = Client();
        var body = new { itemId = movie, quality = "Original" };

        var first = (await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), body))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await WaitForAsync(client, first!, "Ready");
        (await client.PostAsync(new Uri($"/api/v1/offline/{first}/received", UriKind.Relative), null)).EnsureSuccessStatusCode();

        // The iPad asks for the same film: not the phone's, whose copy has gone, but one of its own to fetch.
        var second = (await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), body))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.NotEqual(first, second);
        await WaitForAsync(client, second!, "Ready");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"/api/v1/offline/{second}/file", UriKind.Relative))).StatusCode);
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

    [Fact]
    public async Task ATrackThePhonePlays_ComesAsItIs_AsItsOwnKindOfFile_WhateverSizeWasAsked()
    {
        var (album, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000), ("flac", "flac", ".flac", 30_000_000));
        var client = Client();

        // One size for a track and for its album: the files as they are.
        var trackOptions = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{tracks[0].Id}/download-options", UriKind.Relative));
        var option = Assert.Single(trackOptions.EnumerateArray());
        Assert.Equal("Original", option.GetProperty("quality").GetString());
        Assert.True(option.GetProperty("available").GetBoolean());
        Assert.Equal(new FileInfo(tracks[0].Path).Length, option.GetProperty("estimatedBytes").GetInt64());
        var albumOptions = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{album}/download-options", UriKind.Relative));
        Assert.Equal(tracks.Sum(t => new FileInfo(t.Path).Length), Assert.Single(albumOptions.EnumerateArray()).GetProperty("estimatedBytes").GetInt64());

        // Asked for at the phone's film size: it's the original all the same.
        var created = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = tracks[1].Id, quality = "Medium" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Original", created.GetProperty("quality").GetString());
        Assert.Equal("Track", created.GetProperty("item").GetProperty("kind").GetString());
        var id = created.GetProperty("id").GetString()!;

        var ready = await WaitForAsync(client, id, "Ready");
        Assert.Equal(new FileInfo(tracks[1].Path).Length, ready.GetProperty("sizeBytes").GetInt64());
        Assert.Equal($"{Guid.Parse(id):N}.flac", ready.GetProperty("fileName").GetString());

        var file = await client.GetAsync(new Uri($"/api/v1/offline/{id}/file", UriKind.Relative));
        Assert.Equal("audio/flac", file.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".flac", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);

        // Received: the library's own file stays.
        (await client.PostAsync(new Uri($"/api/v1/offline/{id}/received", UriKind.Relative), null)).EnsureSuccessStatusCode();
        Assert.True(File.Exists(tracks[1].Path));
    }

    [Fact]
    public async Task ATrackThePhoneCantPlay_IsOffered_AtTheSizeOfItsAac()
    {
        var (_, tracks) = await AddAlbumAsync(("ogg", "vorbis", ".ogg", 5_000_000));
        var client = Client();

        var options = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{tracks[0].Id}/download-options", UriKind.Relative));
        var option = Assert.Single(options.EnumerateArray());
        Assert.True(option.GetProperty("available").GetBoolean());
        // 240 seconds at 256 kbps.
        Assert.Equal(256_000L * 240 / 8, option.GetProperty("estimatedBytes").GetInt64());

        var response = await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = tracks[0].Id, quality = "Original" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Queued", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListeningOffline_CountsEveryPlay()
    {
        var (_, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000));
        var client = Client();
        var uri = new Uri($"/api/v1/items/{tracks[0].Id}/offline-progress", UriKind.Relative);

        (await client.PostAsJsonAsync(uri, new { positionSeconds = 0, finished = true, playedAt = DateTime.UtcNow.AddHours(-2) })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(uri, new { positionSeconds = 0, finished = true, playedAt = DateTime.UtcNow.AddHours(-1) })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.SingleAsync(p => p.MediaItemId == tracks[0].Id);
        Assert.Equal(2, state.PlayCount);
    }

    [Fact]
    public async Task AnArtist_HasNoDownloadOptions()
    {
        var (album, _) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000));
        using var scope = _factory.Services.CreateScope();
        var artist = (await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.SingleAsync(i => i.Id == album)).ParentId;

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{artist}/download-options", UriKind.Relative))).StatusCode);
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

    /// <summary>An album of one track per file given: (ffprobe container, audio codec, extension, bitrate), each 240 seconds long.</summary>
    private async Task<(Guid Album, List<(Guid Id, string Path)> Tracks)> AddAlbumAsync(params (string Container, string Codec, string Extension, long Bitrate)[] files)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Band", SortTitle = "band" };
        var album = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Album, Title = "Record", SortTitle = "record", ParentId = artist.Id, Year = 1985 };
        db.AddRange(library, artist, album);
        var tracks = new List<(Guid, string)>();
        foreach (var (file, n) in files.Select((f, i) => (f, i + 1)))
        {
            var path = Path.Combine(_factory.MediaDirectory, $"{Guid.NewGuid():N}{file.Extension}");
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 1024 * n).Select(i => (byte)i).ToArray());
            var track = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Track, Title = $"Song {n}", SortTitle = $"song {n}", ParentId = album.Id, TrackNumber = n };
            db.Add(track);
            db.Add(new MediaFile
            {
                MediaItemId = track.Id, Path = path, Size = 1024 * n, Container = file.Container, DurationSeconds = 240, Bitrate = file.Bitrate,
                AudioCodec = file.Codec, AudioChannels = 2,
                Streams = [new MediaStream { Index = 0, Type = StreamType.Audio, Codec = file.Codec, Channels = 2, IsDefault = true }],
            });
            tracks.Add((track.Id, path));
        }

        await db.SaveChangesAsync();
        return (album.Id, tracks);
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
