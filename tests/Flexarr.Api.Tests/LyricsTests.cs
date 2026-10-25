using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>GET /items/{id}/lyrics: a track's lyrics from the sidecar next to it, synced when the sidecar is LRC.</summary>
public class LyricsTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AnLrcSidecar_GivesSyncedLyrics_AndIsCachedOnDisk()
    {
        var (track, path) = await SeedTrackAsync("02 - News.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[ar:Dire Straits]\n[offset:+500]\n[00:12.50]He sticks to his guns\n[00:16.00]He takes the road as it comes\n");

        var response = await Client().GetAsync(new Uri($"/api/v1/items/{track}/lyrics", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lyrics = await response.Content.ReadFromJsonAsync<LyricsDto>(Json);
        Assert.NotNull(lyrics);
        Assert.True(lyrics.Synced);
        Assert.Equal([new LyricLineDto(12, "He sticks to his guns"), new LyricLineDto(15.5, "He takes the road as it comes")], lyrics.Lines);

        // The parsed answer is kept under the cache directory, named for the file.
        var cached = Directory.GetFiles(Path.Combine(factory.DataDirectory, "cache", "lyrics"));
        Assert.Contains(cached, f => Path.GetFileName(f).EndsWith(".json", StringComparison.Ordinal));

        // Taking the sidecar away is noticed: the cache is keyed by what's on disk, not just the file id.
        File.Delete(Path.ChangeExtension(path, ".lrc"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{track}/lyrics", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ATextSidecar_GivesUnsyncedLyrics()
    {
        var (track, path) = await SeedTrackAsync("03 - Where Do You Think You're Going.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".txt"), "Where do you think you're going?\nDon't you know it's dark outside?\n");

        var lyrics = await Client().GetFromJsonAsync<LyricsDto>(new Uri($"/api/v1/items/{track}/lyrics", UriKind.Relative), Json);

        Assert.NotNull(lyrics);
        Assert.False(lyrics.Synced);
        Assert.Equal([new LyricLineDto(null, "Where do you think you're going?"), new LyricLineDto(null, "Don't you know it's dark outside?")], lyrics.Lines);
    }

    [Fact]
    public async Task NoLyrics_NoTrack_Is404()
    {
        var (track, _) = await SeedTrackAsync("01 - Once Upon a Time in the West.flac");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var album = db.MediaItems.First(i => i.Id == db.MediaItems.First(t => t.Id == track).ParentId);

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{track}/lyrics", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{album.Id}/lyrics", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{Guid.NewGuid()}/lyrics", UriKind.Relative))).StatusCode);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>One track on one album, whose file is a path in a scratch folder (no audio file; the sidecars are real).</summary>
    private async Task<(Guid Track, string Path)> SeedTrackAsync(string fileName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"music-{Guid.NewGuid():N}", "Dire Straits", "Communiqué (1979)")).FullName;
        var path = Path.Combine(folder, fileName);

        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Dire Straits", SortTitle = "dire straits" };
        var album = new MediaItem { LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = "Communiqué", SortTitle = "communiqué", Year = 1979 };
        var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = Path.GetFileNameWithoutExtension(fileName), SortTitle = "track" };
        db.AddRange(library, artist, album, track, new MediaFile { MediaItemId = track.Id, Path = path, Container = "flac", AudioCodec = "flac" });
        await db.SaveChangesAsync();
        return (track.Id, path);
    }
}
