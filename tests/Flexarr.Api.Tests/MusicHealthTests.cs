using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Library health's music checks, and Merge.</summary>
public sealed class MusicHealthTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private Library _library = null!;
    private MediaItem _beyonce = null!;
    private MediaItem _beyonceAccented = null!;
    private MediaItem _lemonade = null!;
    private MediaItem _lemonadeAgain = null!;
    private MediaItem _repeated = null!;
    private MediaItem _coverless = null!;

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        _library = new Library { Name = "Music", Type = LibraryType.Music };
        _beyonceAccented = Artist("Beyoncé");
        _beyonce = Artist("Beyonce");
        var placeholder = Artist("BEYONCE!");
        placeholder.Placeholder = true; // followed, nothing filed: never a split
        var theBeatles = Artist("The Beatles");
        var beatles = Artist("Beatles");
        var adele = Artist("Adele");
        _lemonade = Album(_beyonceAccented, "Lemonade", "file:///music/Beyoncé/Lemonade/cover.jpg", "mb-lemonade");
        _lemonadeAgain = Album(_beyonce, "Lemonade (Deluxe)", "embedded:///music/Beyonce/Lemonade/01.flac", "mb-lemonade");
        _repeated = Album(theBeatles, "Abbey Road", "file:///music/The Beatles/Abbey Road/cover.jpg", null);
        _coverless = Album(adele, "25", null, null);
        db.AddRange(_library, _beyonceAccented, _beyonce, placeholder, theBeatles, beatles, adele, _lemonade, _lemonadeAgain, _repeated, _coverless,
            Track(_lemonade, 1), Track(_lemonadeAgain, 12), Track(_repeated, 1), Track(_repeated, 1), Track(_repeated, 2), Track(_coverless, 1));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_FindsCoverlessAlbums_OneReleaseTwice_TracksTwice_AndSplitArtists()
    {
        var health = await HealthAsync();

        Assert.Equal(["25"], health.GetProperty("albumsWithoutCover").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        var duplicate = Assert.Single(health.GetProperty("duplicateAlbums").EnumerateArray());
        Assert.Equal([_lemonade.Id, _lemonadeAgain.Id], Ids(duplicate.GetProperty("albums")).Order());
        var repeated = Assert.Single(health.GetProperty("albumsWithRepeatedTracks").EnumerateArray());
        Assert.Equal((_repeated.Id, 1, "The Beatles"), (repeated.GetProperty("album").GetProperty("id").GetGuid(), repeated.GetProperty("repeated").GetInt32(), repeated.GetProperty("artist").GetString()));
        var split = health.GetProperty("splitArtists").EnumerateArray().Select(s => s.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("title").GetString()).Order().ToList()).ToList();
        Assert.Equal([["Beatles", "The Beatles"], ["Beyonce", "Beyoncé"]], split.OrderBy(s => s[0]));

        // Albums are titles too: four of them, each with something wrong (both copies of Lemonade).
        var score = health.GetProperty("score");
        Assert.Equal((4, 4, 1, 1, 1, 2, 0), (score.GetProperty("titles").GetInt32(), score.GetProperty("withProblems").GetInt32(), score.GetProperty("albumsWithoutCover").GetInt32(),
            score.GetProperty("duplicateAlbums").GetInt32(), score.GetProperty("albumsWithRepeatedTracks").GetInt32(), score.GetProperty("splitArtists").GetInt32(), score.GetProperty("score").GetInt32()));
    }

    [Fact]
    public async Task MergingArtists_MovesTheAlbums_AndRemembersTheOtherSpelling()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/library-health/merge-artists", UriKind.Relative), new { keep = _beyonceAccented.Id, merge = new[] { _beyonce.Id } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.False(await db.MediaItems.AnyAsync(i => i.Id == _beyonce.Id));
        Assert.Equal([_lemonade.Id, _lemonadeAgain.Id], (await db.MediaItems.Where(i => i.ParentId == _beyonceAccented.Id).Select(i => i.Id).ToListAsync()).Order());
        var alias = await db.MusicAliases.SingleAsync();
        Assert.Equal(("artist:beyonce", _beyonceAccented.Id), (alias.Key, alias.ItemId));
        Assert.DoesNotContain(health(await HealthAsync()), s => s.Contains("Beyonce"));

        static IEnumerable<string> health(JsonElement h) => h.GetProperty("splitArtists").EnumerateArray()
            .SelectMany(s => s.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("title").GetString()!));
    }

    [Fact]
    public async Task MergingAlbums_MovesTheTracks_AndRemembersTheFolderAndTitle()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/library-health/merge-albums", UriKind.Relative), new { keep = _lemonade.Id, merge = new[] { _lemonadeAgain.Id } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.Equal(2, await db.MediaItems.CountAsync(i => i.ParentId == _lemonade.Id));
        Assert.Equal([$"album:{_beyonce.Id}:lemonade (deluxe)", "path:/music/beyonce/lemonade (deluxe)"],
            (await db.MusicAliases.Select(a => a.Key).ToListAsync()).Select(k => k.ToLowerInvariant()).Order());
        Assert.Empty((await HealthAsync()).GetProperty("duplicateAlbums").EnumerateArray());
    }

    [Fact]
    public async Task Merge_RefusesAcrossLibraries_AndKinds()
    {
        Guid elsewhere;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var other = new Library { Name = "More music", Type = LibraryType.Music };
            var artist = new MediaItem { LibraryId = other.Id, Kind = MediaKind.Artist, Title = "Beyoncé", SortTitle = "beyonce" };
            db.AddRange(other, artist);
            await db.SaveChangesAsync();
            elsewhere = artist.Id;
        }

        var acrossLibraries = await Client().PostAsJsonAsync(new Uri("/api/v1/library-health/merge-artists", UriKind.Relative), new { keep = _beyonceAccented.Id, merge = new[] { elsewhere } });
        var anAlbum = await Client().PostAsJsonAsync(new Uri("/api/v1/library-health/merge-artists", UriKind.Relative), new { keep = _beyonceAccented.Id, merge = new[] { _lemonade.Id } });
        var nothing = await Client().PostAsJsonAsync(new Uri("/api/v1/library-health/merge-artists", UriKind.Relative), new { keep = _beyonceAccented.Id, merge = new[] { _beyonceAccented.Id } });

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.BadRequest), (acrossLibraries.StatusCode, anAlbum.StatusCode, nothing.StatusCode));
    }

    private async Task<JsonElement> HealthAsync() => await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative));

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static IEnumerable<Guid> Ids(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid());

    private MediaItem Artist(string name) => new() { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = name, SortTitle = name.ToLowerInvariant() };

    private MediaItem Album(MediaItem artist, string title, string? cover, string? musicBrainzId) => new()
    {
        LibraryId = _library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = title, SortTitle = title.ToLowerInvariant(),
        Path = $"/music/{artist.Title}/{title}", PosterUrl = cover, MusicBrainzId = musicBrainzId,
    };

    private MediaItem Track(MediaItem album, int number) => new()
    {
        LibraryId = _library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = $"Track {number}", SortTitle = $"{number:D4}", TrackNumber = number,
    };
}
