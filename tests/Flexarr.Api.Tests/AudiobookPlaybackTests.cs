using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Books;
using Flexarr.Api.Playback;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Playing a book: its files on one timeline, progress newest-wins, finishing, bookmarks, speed and listening settings.</summary>
public class AudiobookPlaybackTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task TheManifest_PutsTheFilesOnOneTimeline_AndConvertsWhatThePlayerCantDecode()
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.wma", "wmav2", 300), ("03.mp3", "mp3", 900));

        var manifest = await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book?codecs=aac,mp3");

        Assert.Equal(1800, manifest.DurationSeconds);
        Assert.Equal([(0, 0.0, false), (1, 600.0, true), (2, 900.0, false)], manifest.Files.Select(f => (f.Index, f.OffsetSeconds, f.Converted)));
        Assert.Equal($"/api/v1/items/{book}/book/files/1?convert=aac", manifest.Files[1].Url);
        Assert.Equal(1, manifest.Speed);
        Assert.Equal(new BookListeningDto(1, 30, 30, true), manifest.Listening);
    }

    [Fact]
    public async Task PlayingABook_GivesTheSessionsOwnAddresses_AndServesTheFiles()
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.mp3", "mp3", 600));
        await Client().PutAsJsonAsync(Url($"/api/v1/items/{book}/progress"), new { positionSeconds = 700, device = "Kitchen speaker" });

        var started = await Client().PostAsJsonAsync(Url("/api/v1/playback"), new { itemId = book, profile = new { audioCodecs = new[] { "aac", "mp3" } }, deviceName = "Dan's iPhone" });
        started.EnsureSuccessStatusCode();
        var info = (await started.Content.ReadFromJsonAsync<PlaybackInfoDto>(Json))!;

        Assert.Equal(700, info.StartPositionSeconds);
        Assert.Equal(1200, info.DurationSeconds);
        Assert.Equal("Kitchen speaker", info.Book!.Progress.Device);
        Assert.StartsWith($"/api/v1/playback/{info.SessionId}/book/", info.Book.Files[1].Url, StringComparison.Ordinal);
        // No key needed: the session id is the credential, so AirPlay and Chromecast can fetch it.
        var file = await factory.CreateClient().GetAsync(Url(info.Book.Files[1].Url));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("02.mp3", await file.Content.ReadAsStringAsync());
        Assert.Equal("audio/mpeg", file.Content.Headers.ContentType?.MediaType);

        // The session's progress is on the book's timeline, from that device.
        (await Client().PostAsJsonAsync(Url($"/api/v1/playback/{info.SessionId}/progress"), new { positionSeconds = 950 })).EnsureSuccessStatusCode();
        var progress = (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress;
        Assert.Equal((950.0, "Dan's iPhone"), (progress.PositionSeconds, progress.Device));
    }

    [Theory]
    [InlineData(null, false)] // an app from before books plays the first file as a video: its end isn't the book's
    [InlineData("Dan's iPhone", true)]
    public async Task AnEnd_FinishesABookOfSeveralFiles_OnlyFromAPlayerThatKnowsBooks(string? deviceName, bool finished)
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.mp3", "mp3", 600));
        var started = await Client().PostAsJsonAsync(Url("/api/v1/playback"), new { itemId = book, profile = new { audioCodecs = new[] { "aac", "mp3" } }, deviceName });
        var info = (await started.Content.ReadFromJsonAsync<PlaybackInfoDto>(Json))!;

        (await Client().PostAsJsonAsync(Url($"/api/v1/playback/{info.SessionId}/progress"), new { positionSeconds = 600, ended = true })).EnsureSuccessStatusCode();

        Assert.Equal(finished, (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress.Finished);
    }

    [Fact]
    public async Task AnAppFromBeforeBooks_DoesntMoveThePlaceInABookOfSeveralFiles()
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.mp3", "mp3", 600));
        (await Client().PutAsJsonAsync(Url($"/api/v1/items/{book}/progress"), new { positionSeconds = 900.0, device = "Dan's iPhone" })).EnsureSuccessStatusCode();
        var started = await Client().PostAsJsonAsync(Url("/api/v1/playback"), new { itemId = book, profile = new { audioCodecs = new[] { "aac", "mp3" } } });
        var info = (await started.Content.ReadFromJsonAsync<PlaybackInfoDto>(Json))!;

        (await Client().PostAsJsonAsync(Url($"/api/v1/playback/{info.SessionId}/progress"), new { positionSeconds = 120 })).EnsureSuccessStatusCode();

        Assert.Equal(900.0, (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress.PositionSeconds);
    }

    [Fact]
    public async Task ADownload_GetsEachFile_AndCanResumePartWay()
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.mp3", "mp3", 600));
        var manifest = await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book?codecs=aac,mp3");

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(manifest.Files[1].Url));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, null);
        var rest = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        Assert.Equal(".mp3", await rest.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/2"))).StatusCode);
        // Without a key, the item's files are refused: only a session's address is its own credential.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url(manifest.Files[0].Url))).StatusCode);
    }

    [Fact]
    public async Task AFileEveryPlayerDecodes_IsNeverConverted_WhateverTheAddressSays()
    {
        var book = await SeedAsync(("01.mp3", "mp3", 600), ("02.m4b", "aac", 600));

        foreach (var (index, name, type) in new[] { (0, "01.mp3", "audio/mpeg"), (1, "02.m4b", "audio/mp4") })
        {
            var file = await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/{index}?convert=aac"));
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(name, await file.Content.ReadAsStringAsync());
            Assert.Equal(type, file.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Progress_TheNewestRecordingWins()
    {
        var book = await SeedAsync(("book.m4b", "aac", 36000));
        var now = DateTime.UtcNow;

        await PutProgressAsync(book, 5000, "iPhone", now.AddMinutes(-10));
        var stale = await PutProgressAsync(book, 100, "Old iPad", now.AddHours(-2)); // listened offline long ago, sent now
        var newer = await PutProgressAsync(book, 6000, "Web", now);

        Assert.Equal((5000.0, "iPhone"), (stale.PositionSeconds, stale.Device));
        Assert.Equal((6000.0, "Web"), (newer.PositionSeconds, newer.Device));
    }

    [Fact]
    public async Task TwoFirstReportsAtOnce_BothCount()
    {
        var book = await SeedAsync(("book.m4b", "aac", 36000));
        string connection;
        using (var scope = factory.Services.CreateScope())
        {
            connection = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Database.GetConnectionString()!;
        }

        // The other report inserts the row between this one looking for it and saving.
        var other = new InsertFirst(connection, book);
        await using var db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite(connection).AddInterceptors(other).Options);

        var progress = await BookPlayback.RecordAsync(db, book, PlaybackState.DefaultUser, new BookProgressRequest(5000, "iPhone", DateTime.UtcNow), CancellationToken.None);

        Assert.True(other.Inserted);
        Assert.Equal((5000.0, "iPhone"), (progress!.PositionSeconds, progress.Device));
    }

    private sealed class InsertFirst(string connection, Guid book) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Inserted)
            {
                Inserted = true;
                await using var db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite(connection).Options);
                db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = book, PositionSeconds = 100, LastPlayedAt = DateTime.UtcNow.AddMinutes(-1) });
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    [Theory]
    [InlineData(35000, false)] // more than two minutes and 2% from the end
    [InlineData(35290, true)]  // past 98%
    [InlineData(36000 - 119, true)] // within the last two minutes
    public async Task ABookIsFinished_NearItsEnd(double position, bool finished)
    {
        var book = await SeedAsync(("book.m4b", "aac", 36000));

        var progress = await PutProgressAsync(book, position, "iPhone", DateTime.UtcNow);

        Assert.Equal(finished, progress.Finished);
        Assert.Equal(finished, progress.FinishedAt is not null);
    }

    [Fact]
    public async Task MarkingABookWatched_FinishesIt_AndUnwatchedForgetsWhen()
    {
        var book = await SeedAsync(("book.m4b", "aac", 3600));

        (await Client().PostAsJsonAsync(Url($"/api/v1/items/{book}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        var finished = (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress;
        (await Client().PostAsJsonAsync(Url($"/api/v1/items/{book}/watched"), new { watched = false })).EnsureSuccessStatusCode();
        var unfinished = (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress;

        Assert.True(finished.Finished);
        Assert.NotNull(finished.FinishedAt);
        Assert.False(unfinished.Finished);
        Assert.Null(unfinished.FinishedAt);
    }

    [Fact]
    public async Task ListenAgain_StartsOver_AndKeepsWhenItWasFinished()
    {
        var book = await SeedAsync(("book.m4b", "aac", 3600));
        (await Client().PostAsJsonAsync(Url($"/api/v1/items/{book}/finished"), new { finished = true })).EnsureSuccessStatusCode();
        var finishedAt = (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress.FinishedAt;

        (await Client().PostAsync(Url($"/api/v1/items/{book}/listen-again"), null)).EnsureSuccessStatusCode();

        var progress = (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Progress;
        Assert.False(progress.Finished);
        Assert.Equal(0, progress.PositionSeconds);
        Assert.NotNull(finishedAt);
        Assert.Equal(finishedAt, progress.FinishedAt);
    }

    [Fact]
    public async Task Bookmarks_AddEditListAndRemove()
    {
        var book = await SeedAsync(("book.m4b", "aac", 3600));

        var added = await (await Client().PostAsJsonAsync(Url($"/api/v1/items/{book}/bookmarks"), new { positionSeconds = 1234.5, note = "  the twist  " }))
            .Content.ReadFromJsonAsync<BookmarkDto>(Json);
        await Client().PostAsJsonAsync(Url($"/api/v1/items/{book}/bookmarks"), new { positionSeconds = 60 });
        await Client().PutAsJsonAsync(Url($"/api/v1/bookmarks/{added!.Id}"), new { note = "the big twist" });

        var list = await GetAsync<List<BookmarkDto>>($"/api/v1/items/{book}/bookmarks");
        Assert.Equal([(60.0, (string?)null), (1234.5, "the big twist")], list.Select(b => (b.PositionSeconds, b.Note)));

        Assert.Equal(HttpStatusCode.NoContent, (await Client().DeleteAsync(Url($"/api/v1/bookmarks/{added.Id}"))).StatusCode);
        Assert.Single(await GetAsync<List<BookmarkDto>>($"/api/v1/items/{book}/bookmarks"));
    }

    [Fact]
    public async Task ABookCanBePinnedAt1x_WhenTheProfilesDefaultIsFaster()
    {
        var book = await SeedAsync(("book.m4b", "aac", 3600));
        var listening = await (await Client().PutAsJsonAsync(Url("/api/v1/books/listening"), new { speed = 1.53, skipBackSeconds = 15 }))
            .Content.ReadFromJsonAsync<BookListeningDto>(Json);

        Assert.Equal(new BookListeningDto(1.55, 15, 30, true), listening);
        Assert.Equal(1.55, (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Speed);

        (await Client().PutAsJsonAsync(Url($"/api/v1/items/{book}/playback-speed"), new { speed = 1.0 })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book")).Speed);

        await Client().PutAsJsonAsync(Url("/api/v1/books/listening"), new { speed = 1.0, skipBackSeconds = 30 });
    }

    [Fact]
    public async Task AProfileWithNoListeningSettings_ListensAt1x_WithThirtySecondSkips()
    {
        var book = await SeedAsync(("book.m4b", "aac", 3600));
        await SetOwnersListeningAsync(speed: 0, skip: 0);
        try
        {
            Assert.Equal(new BookListeningDto(1, 30, 30, true), await GetAsync<BookListeningDto>("/api/v1/books/listening"));
            var manifest = await GetAsync<BookManifestDto>($"/api/v1/items/{book}/book");
            Assert.Equal(1, manifest.Speed);
            Assert.Equal(new BookListeningDto(1, 30, 30, true), manifest.Listening);

            // 1× is the default, so the book doesn't keep it as its own.
            (await Client().PutAsJsonAsync(Url($"/api/v1/items/{book}/playback-speed"), new { speed = 1.0 })).EnsureSuccessStatusCode();
            using var scope = factory.Services.CreateScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().ShowPlaybackSpeeds.AnyAsync(s => s.SeriesId == book));
        }
        finally
        {
            await SetOwnersListeningAsync(speed: 1, skip: 30);
        }
    }

    private async Task SetOwnersListeningAsync(double speed, int skip)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Profiles.Where(p => p.Id == "owner").ExecuteUpdateAsync(p => p
            .SetProperty(x => x.AudiobookSpeed, speed)
            .SetProperty(x => x.AudiobookSkipBackSeconds, skip)
            .SetProperty(x => x.AudiobookSkipForwardSeconds, skip));
    }

    [Theory]
    [InlineData(4.0, null)]
    [InlineData(null, 20)]
    public async Task ListeningSettings_StayInRange(double? speed, int? skip)
    {
        var response = await Client().PutAsJsonAsync(Url("/api/v1/books/listening"), new { speed, skipBackSeconds = skip });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_AFileThePlayerCantDecode_IsConvertedToAac()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var wav = Path.Combine(factory.MediaDirectory, $"{Guid.NewGuid():N}.wav");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-t", "2", "-c:a", "pcm_s16le", wav);
        var book = await SeedFilesAsync([(wav, "pcm_s16le", 2)]);

        var response = await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/0?convert=aac"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
    }

    [Fact]
    public async Task AKidsProfile_CantReachABook()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2468");
        var book = await SeedFilesAsync(server, [(Path.Combine(server.MediaDirectory, "book.m4b"), "aac", 3600)]);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var created = await admin.PostAsJsonAsync(Url("/api/v1/profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var kidId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var device = (await (await admin.PostAsJsonAsync(Url("/api/v1/session/device"), new { deviceName = "Playroom iPad" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var kid = server.CreateClient();
        kid.DefaultRequestHeaders.Add("X-Api-Key", device);
        Assert.Equal(HttpStatusCode.OK, (await kid.PostAsJsonAsync(Url($"/api/v1/profiles/{kidId}/select"), new { })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(Url($"/api/v1/items/{book}/book"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(Url($"/api/v1/items/{book}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.PutAsJsonAsync(Url($"/api/v1/items/{book}/progress"), new { positionSeconds = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Url($"/api/v1/items/{book}/book"))).StatusCode);
    }

    private async Task<BookProgressDto> PutProgressAsync(Guid book, double position, string device, DateTime at)
    {
        var response = await Client().PutAsJsonAsync(Url($"/api/v1/items/{book}/progress"), new { positionSeconds = position, device, recordedAt = at });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BookProgressDto>(Json))!;
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await Client().GetAsync(Url(url));
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A book of small files whose text is their name, each said to last the given seconds with the given sound.</summary>
    private Task<Guid> SeedAsync(params (string Name, string Codec, double Seconds)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(folder, file.Name), file.Name);
        }

        return SeedFilesAsync([.. files.Select(f => (Path.Combine(folder, f.Name), f.Codec, f.Seconds))]);
    }

    private Task<Guid> SeedFilesAsync(IReadOnlyList<(string Path, string Codec, double Seconds)> files) => SeedFilesAsync(factory, files);

    private static async Task<Guid> SeedFilesAsync(FlexarrApiFactory server, IReadOnlyList<(string Path, string Codec, double Seconds)> files)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "A Book", SortTitle = "book", Path = Path.GetDirectoryName(files[0].Path),
            Book = new BookDetails { DurationSeconds = files.Sum(f => f.Seconds) },
            Files = [.. files.Select((f, i) => new MediaFile { Path = f.Path, AudioCodec = f.Codec, DurationSeconds = f.Seconds, BookOrder = i, Size = 10 })],
        };
        db.AddRange(library, book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{program} failed: {await errors}");
    }
}
