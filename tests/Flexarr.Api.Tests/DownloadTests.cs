using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void LeftoverPartials_AreDeleted_PastAFolderTheServerCantRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("flexarr-partials-").FullName;
        var locked = Directory.CreateDirectory(Path.Combine(root, "a-locked")).FullName;
        var show = Directory.CreateDirectory(Path.Combine(root, "b-show")).FullName;
        var partial = Path.Combine(show, "episode.mp3.part.1");
        File.WriteAllText(partial, "half");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            _factory.Services.GetRequiredService<Downloads.DownloadPreparer>().DeletePartials(root, recurse: true);

            Assert.False(File.Exists(partial));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

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

    [Fact]
    public async Task AConversionTheHardwareEncoderFails_IsDoneAgainInSoftware()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffmpeg: the hardware encoder fails; libx264 writes the file (its last argument).
        var ffmpeg = Path.Combine(_factory.DataDirectory, "fake-ffmpeg");
        await File.WriteAllTextAsync(ffmpeg, """
            #!/bin/sh
            case "$*" in *libx264*) ;; *) echo "No VA display found" >&2; exit 1 ;; esac
            for last; do :; done
            echo x > "$last"
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var server = _factory.WithWebHostBuilder(b => b
            .UseSetting("Flexarr:FfmpegPath", ffmpeg)
            .UseSetting("Flexarr:Transcoding:HardwareAcceleration", "Vaapi"));
        var (movie, _) = await AddMovieAsync("matroska,webm", "mpeg2video", "aac");
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var created = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = movie, quality = "Medium" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        await WaitForAsync(client, created.GetProperty("id").GetString()!, "Ready", server.Services);
    }

    [Fact]
    public async Task AConversionThatThrows_EndsAsFailed_NotPreparingForever()
    {
        using var server = _factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:FfmpegPath", Path.Combine(_factory.DataDirectory, "no-such-ffmpeg")));
        var (movie, _) = await AddMovieAsync("matroska,webm", "mpeg2video", "aac");
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var created = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = movie, quality = "Medium" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        await WaitForAsync(client, created.GetProperty("id").GetString()!, "Failed", server.Services);
        Assert.Empty(Directory.EnumerateFiles(Downloads.DownloadPreparer.Folder(
            server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions>>().CurrentValue), "*.part.*"));
    }

    [Fact]
    public async Task DownloadsSentAsTheyAre_AreAllReadyInOneRound_PastTheOnesWaitingToBeConverted()
    {
        // A film to convert is first in the queue, then an album: four tracks the phone plays, and one it can't.
        var (movie, _) = await AddMovieAsync("matroska,webm", "mpeg2video", "aac");
        var (_, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000), ("flac", "flac", ".flac", 30_000_000), ("ogg", "vorbis", ".ogg", 5_000_000),
            ("mp3", "mp3", ".mp3", 3_000_000), ("mp3", "mp3", ".mp3", 3_000_000));
        var client = Client();
        async Task<string> AskAsync(Guid item, string quality) =>
            (await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = item, quality })).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;
        var film = await AskAsync(movie, "Medium");
        var songs = new List<string>();
        foreach (var track in tracks)
        {
            songs.Add(await AskAsync(track.Id, "Original"));
        }

        var preparer = _factory.Services.GetRequiredService<Downloads.DownloadPreparer>();
        await preparer.SendAsIsAsync(CancellationToken.None);

        async Task<JsonElement> NowAsync(string id) => await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/offline/{id}", UriKind.Relative));
        var statuses = new List<string?>();
        foreach (var song in songs)
        {
            statuses.Add((await NowAsync(song)).GetProperty("status").GetString());
        }

        Assert.Equal(["Ready", "Ready", "Queued", "Ready", "Ready"], statuses);
        Assert.Equal("Queued", (await NowAsync(film)).GetProperty("status").GetString());
        var ready = await NowAsync(songs[1]);
        Assert.Equal(new FileInfo(tracks[1].Path).Length, ready.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1, ready.GetProperty("progress").GetDouble());
        Assert.Equal(await File.ReadAllBytesAsync(tracks[1].Path), await client.GetByteArrayAsync(new Uri($"/api/v1/offline/{songs[1]}/file", UriKind.Relative)));

        // A download deleted, or already with its device, isn't brought back by a round that had it down as queued.
        (await client.PostAsync(new Uri($"/api/v1/offline/{songs[0]}/received", UriKind.Relative), null)).EnsureSuccessStatusCode();
        await preparer.SendAsIsAsync(CancellationToken.None);
        Assert.Equal("Received", (await NowAsync(songs[0])).GetProperty("status").GetString());

        // The ones that need converting still get their turn, one at a time, as before.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.Equal(2, await db.Downloads.CountAsync(d => d.Status == DownloadStatus.Queued));
    }

    [Fact]
    public async Task DownloadsSentAsTheyAre_AreReady_WhileAFilmIsBeingConverted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffmpeg: a conversion (its last argument is the file to write) that's still going when the test
        // ends. Anything else it's asked (which encoders it has) it answers at once.
        var ffmpeg = Path.Combine(_factory.DataDirectory, "slow-ffmpeg");
        await File.WriteAllTextAsync(ffmpeg, """
            #!/bin/sh
            case "$*" in *.part.mp4) sleep 60 ;; esac
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var server = _factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:FfmpegPath", ffmpeg));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var (movie, _) = await AddMovieAsync("matroska,webm", "mpeg2video", "aac");
        var (_, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000), ("flac", "flac", ".flac", 30_000_000));
        async Task<string> AskAsync(Guid item, string quality) =>
            (await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = item, quality })).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;
        async Task<string?> StatusAsync(string id) =>
            (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/offline/{id}", UriKind.Relative))).GetProperty("status").GetString();
        async Task UntilAsync(string id, string status)
        {
            for (var i = 0; i < 150 && await StatusAsync(id) != status; i++)
            {
                await Task.Delay(100);
            }

            Assert.Equal(status, await StatusAsync(id));
        }

        var film = await AskAsync(movie, "Medium");
        // The worker itself this time, on its own clocks (tests otherwise run its rounds by hand).
        var preparer = server.Services.GetRequiredService<Downloads.DownloadPreparer>();
        await preparer.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(film, "Preparing");
            var songs = new[] { await AskAsync(tracks[0].Id, "Original"), await AskAsync(tracks[1].Id, "Original") };

            await UntilAsync(songs[0], "Ready");
            await UntilAsync(songs[1], "Ready");
            Assert.Equal("Preparing", await StatusAsync(film));
        }
        finally
        {
            await preparer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithNothingQueued_ThePreparerAsksTheDatabaseNothing_UntilADownloadWakesIt()
    {
        var sql = new Statements();
        using var server = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<FlexarrDbContext>(o => o.AddInterceptors(sql))));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var (_, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000));

        var preparer = server.Services.GetRequiredService<Downloads.DownloadPreparer>();
        await preparer.StartAsync(CancellationToken.None);
        try
        {
            // Its first look (there's nothing), then longer than the three seconds it used to look again after.
            await Task.Delay(500);
            sql.Run.Clear();
            await Task.Delay(4000);
            Assert.Empty(sql.Run);

            // A download queued now isn't left for the next hourly look: queueing it wakes the preparer.
            var id = (await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = tracks[0].Id, quality = "Original" }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            async Task<string?> StatusAsync() =>
                (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/offline/{id}", UriKind.Relative))).GetProperty("status").GetString();
            for (var i = 0; i < 100 && await StatusAsync() != "Ready"; i++)
            {
                await Task.Delay(100);
            }

            Assert.Equal("Ready", await StatusAsync());

            // And with that one ready it goes quiet again (one more look finds nothing left).
            await Task.Delay(3500);
            sql.Run.Clear();
            await Task.Delay(4000);
            Assert.Empty(sql.Run);
        }
        finally
        {
            await preparer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARound_SaysWhetherDownloadsAreStillWaiting()
    {
        var preparer = _factory.Services.GetRequiredService<Downloads.DownloadPreparer>();
        Assert.False(await preparer.RunOnceAsync(CancellationToken.None));

        var (_, tracks) = await AddAlbumAsync(("mp3", "mp3", ".mp3", 3_000_000));
        (await Client().PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = tracks[0].Id, quality = "Original" })).EnsureSuccessStatusCode();

        // It found one to make ready, so another round is due; that one finds nothing, and the clock can stop.
        Assert.True(await preparer.RunOnceAsync(CancellationToken.None));
        Assert.False(await preparer.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>Every statement the server sent the database.</summary>
    private sealed class Statements : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Run { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Run.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task ACopyThatWouldLeaveTheDriveWithoutRoomToSpare_IsntMade_AndSaysWhy_WhileAFileSentAsItIsStillComes()
    {
        // Were it run, this "ffmpeg" would fail the download another way.
        using var server = _factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:FfmpegPath", Path.Combine(_factory.DataDirectory, "no-such-ffmpeg")));
        var preparer = server.Services.GetRequiredService<Downloads.DownloadPreparer>();
        var asked = new List<string>();
        preparer.FreeBytes = folder =>
        {
            asked.Add(folder);
            return Downloads.DownloadPreparer.SpareBytes + 1000; // room to spare, but not after a ten-minute film
        };
        var (convert, _) = await AddMovieAsync("matroska,webm", "mpeg2video", "aac");
        var (asIs, _) = await AddMovieAsync("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac");
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var refused = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = convert, quality = "Medium" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var failed = await WaitForAsync(client, refused.GetProperty("id").GetString()!, "Failed", server.Services);
        var sent = await (await client.PostAsJsonAsync(new Uri("/api/v1/offline", UriKind.Relative), new { itemId = asIs, quality = "Original" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await WaitForAsync(client, sent.GetProperty("id").GetString()!, "Ready", server.Services);

        Assert.Contains("short of space", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(Downloads.DownloadPreparer.Folder(
            server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions>>().CurrentValue), Assert.Single(asked));
    }

    private async Task<double> PositionAsync(Guid item)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.SingleAsync(p => p.MediaItemId == item)).PositionSeconds;
    }

    private async Task<JsonElement> WaitForAsync(HttpClient client, string id, string status, IServiceProvider? services = null)
    {
        for (var i = 0; i < 100; i++)
        {
            // The worker doesn't run in tests: a round of it, as its timer would.
            await (services ?? _factory.Services).GetRequiredService<Flexarr.Api.Downloads.DownloadPreparer>().RunOnceAsync(CancellationToken.None);
            var download = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/offline/{id}", UriKind.Relative));
            if (download.GetProperty("status").GetString() == status)
            {
                return download;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Download {id} never became {status}");
    }

    [Fact]
    public async Task ADevicesKeptTitles_AreCheckedAgainstTheProfile_AndAMissingFileIsNeverHidden()
    {
        Guid allowed, missing, blocked, aboveAge, missingAboveAge, inHiddenLibrary, missingInHiddenLibrary;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var hidden = new Library { Name = $"Private {Guid.NewGuid():N}", Type = LibraryType.Movies };
            MediaItem Film(Library library, string rating, KidsOverride kids = KidsOverride.None, bool gone = false) => new()
            {
                LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", ContentRating = rating, KidsOverride = kids,
                MissingSince = gone ? DateTime.UtcNow : null,
            };
            var items = new[]
            {
                Film(films, "PG"), Film(films, "PG", gone: true), Film(films, "PG", KidsOverride.Block), Film(films, "R"), Film(films, "R", gone: true),
                Film(hidden, "PG"), Film(hidden, "PG", gone: true),
            };
            db.AddRange(films, hidden);
            db.AddRange(items);
            var main = await db.Profiles.SingleAsync(p => p.Id == PlaybackState.DefaultUser);
            main.MaxRatingAge = 12;
            main.HiddenLibraryIds = [hidden.Id];
            await db.SaveChangesAsync();
            (allowed, missing, blocked, aboveAge, missingAboveAge, inHiddenLibrary, missingInHiddenLibrary) =
                (items[0].Id, items[1].Id, items[2].Id, items[3].Id, items[4].Id, items[5].Id, items[6].Id);
        }

        var deleted = Guid.NewGuid();
        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/offline/check", UriKind.Relative),
            new { itemIds = new[] { allowed, missing, blocked, aboveAge, missingAboveAge, inHiddenLibrary, missingInHiddenLibrary, deleted } });

        response.EnsureSuccessStatusCode();
        var check = (await response.Content.ReadFromJsonAsync<Downloads.OfflineCheckDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal([allowed], check.Allowed);
        Assert.Equal([missing], check.Missing);
        Assert.Equal([blocked, aboveAge, missingAboveAge, inHiddenLibrary, missingInHiddenLibrary], check.Hidden);
        Assert.Equal([deleted], check.Gone);
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
