using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A title's versions over the API: what's offered, what plays, and that a hidden copy stays hidden.</summary>
public sealed class TitleVersionsTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private Guid _hdItem, _uhdItem, _hdFile, _uhdFile, _hdLibrary, _uhdLibrary;

    private static readonly object Limited = new { containers = new[] { "matroska,webm", "mkv" }, videoCodecs = new[] { "hevc" }, audioCodecs = new[] { "aac" }, maxHeight = 1080 };
    private static readonly object Big = new { containers = new[] { "matroska,webm", "mkv" }, videoCodecs = new[] { "hevc" }, audioCodecs = new[] { "aac" }, maxHeight = 2160 };

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var hd = new Library { Name = "HD", Type = LibraryType.Movies };
        var uhd = new Library { Name = "4K", Type = LibraryType.Movies };
        var hdItem = Film(hd);
        var uhdItem = Film(uhd);
        var hdFile = File(hdItem, "Film (2020) 1080p.mkv", 1920, 1080);
        var uhdFile = File(uhdItem, "Film (2020) 2160p.mkv", 3840, 2160);
        db.AddRange(hd, uhd, hdItem, uhdItem, hdFile, uhdFile);
        await db.SaveChangesAsync();
        await TitleLinker.LinkAsync(db, default);
        (_hdItem, _uhdItem, _hdFile, _uhdFile, _hdLibrary, _uhdLibrary) = (hdItem.Id, uhdItem.Id, hdFile.Id, uhdFile.Id, hd.Id, uhd.Id);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static MediaItem Film(Library library) =>
        new() { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", Year = 2020, TmdbId = "603", ContentRating = "PG" };

    private MediaFile File(MediaItem item, string name, int width, int height) => new()
    {
        MediaItemId = item.Id, Path = Path.Combine(_factory.MediaDirectory, name), Container = "matroska,webm", Size = height * 1000L, Width = width, Height = height, VideoCodec = "hevc",
        AudioCodec = "aac", DurationSeconds = 7200, Bitrate = 5_000_000,
        Streams = [new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Profile = "Main", Width = width, Height = height },
            new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true }],
    };

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private async Task<Guid> PlayAsync(Guid item, object device, Guid? fileId = null)
    {
        var response = await Client().PostAsJsonAsync(Api("/playback"), new { itemId = item, profile = device, fileId });
        var info = await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        return info.GetProperty("fileId").GetGuid();
    }

    private async Task HideFourKAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = await db.Profiles.SingleAsync(p => p.Id == PlaybackState.DefaultUser);
        main.HiddenLibraryIds = [_uhdLibrary];
        await db.SaveChangesAsync();
        Flexarr.Api.Profiles.LibraryVisibility.KidsSettingsChanged();
    }

    [Fact]
    public async Task Details_OfferEveryVersion_FromEitherCopy_BestFirst()
    {
        foreach (var item in new[] { _hdItem, _uhdItem })
        {
            var detail = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{item}"));
            var versions = detail.GetProperty("versions").EnumerateArray().ToList();
            Assert.Equal([_uhdFile, _hdFile], versions.Select(v => v.GetProperty("fileId").GetGuid()));
            Assert.Equal(["4K", "1080p"], versions.Select(v => v.GetProperty("label").GetString()));
            Assert.All(versions, v => Assert.False(v.GetProperty("chosen").GetBoolean()));
            // `files` stays this item's own.
            Assert.Single(detail.GetProperty("files").EnumerateArray());
        }
    }

    [Fact]
    public async Task Details_OfASingleVersion_HaveNoVersions()
    {
        await HideFourKAsync();
        var detail = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}"));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("versions").ValueKind);
    }

    [Fact]
    public async Task Playback_PicksAcrossCopies_ForTheDevice()
    {
        Assert.Equal(_uhdFile, await PlayAsync(_hdItem, Big));
        Assert.Equal(_hdFile, await PlayAsync(_uhdItem, Limited));
    }

    [Fact]
    public async Task AChoice_IsRemembered_OnAnyCopy_UntilCleared()
    {
        var set = await Client().PutAsJsonAsync(Api($"/items/{_uhdItem}/version"), new { fileId = _hdFile });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        Assert.Equal(_hdFile, await PlayAsync(_uhdItem, Big));
        Assert.Equal(_hdFile, await PlayAsync(_hdItem, Big));
        var detail = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}"));
        Assert.True(detail.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("fileId").GetGuid() == _hdFile).GetProperty("chosen").GetBoolean());

        await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/version"), new { fileId = (Guid?)null });
        Assert.Equal(_uhdFile, await PlayAsync(_hdItem, Big));
    }

    [Fact]
    public async Task AFileAskedForByName_Plays_WhateverWasChosen()
    {
        await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/version"), new { fileId = _hdFile });
        Assert.Equal(_uhdFile, await PlayAsync(_hdItem, Big, _uhdFile));
    }

    [Fact]
    public async Task AHiddenCopy_CannotBePlayedChosenOrSeen()
    {
        await HideFourKAsync();

        // Asked for by name, it isn't reached: the copy this profile can see plays.
        Assert.Equal(_hdFile, await PlayAsync(_hdItem, Big, _uhdFile));
        Assert.Equal(_hdFile, await PlayAsync(_hdItem, Big));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/version"), new { fileId = _uhdFile })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(Api($"/items/{_uhdItem}"))).StatusCode);
    }

    [Fact]
    public async Task AnUnknownFile_CannotBeChosen() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/version"), new { fileId = Guid.NewGuid() })).StatusCode);

    [Fact]
    public async Task SoundSync_GoesToTheFileBeingPlayed_AndToTheTitleOtherwise()
    {
        async Task<double> DelayOf(Guid file)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            return await db.MediaFiles.AsNoTracking().Where(f => f.Id == file).Select(f => f.AudioDelaySeconds).FirstAsync();
        }

        // Nothing playing: all of this item's own files, as ever.
        await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/audio-delay"), new { seconds = 0.5 });
        Assert.Equal(0.5, await DelayOf(_hdFile));
        Assert.Equal(0, await DelayOf(_uhdFile));

        // Playing the 4K copy from the HD copy's page: that file.
        Assert.Equal(_uhdFile, await PlayAsync(_hdItem, Big));
        await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/audio-delay"), new { seconds = 1.0 });
        Assert.Equal(0.5, await DelayOf(_hdFile));
        Assert.Equal(1.0, await DelayOf(_uhdFile));
    }

    [Fact]
    public async Task Choices_AreEachProfilesOwn()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var other = new Profile { Id = "other", Name = "Other" };
            db.Add(other);
            db.VersionChoices.Add(new VersionChoice { ProfileId = other.Id, MediaItemId = _hdItem, MediaFileId = _hdFile, Label = "1080p" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(_uhdFile, await PlayAsync(_hdItem, Big));
    }

    [Fact]
    public async Task Lists_ShowALinkedTitleOnce_AndALibraryKeepsItsOwn()
    {
        Guid person;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var genre = new Genre { Name = "Mystery" };
            var actor = new Person { Name = "Sam Example" };
            var items = await db.MediaItems.Where(i => i.Kind == MediaKind.Movie).Include(i => i.Genres).ToListAsync();
            foreach (var item in items)
            {
                item.Genres.Add(genre);
                db.Add(new Credit { Person = actor, MediaItemId = item.Id, Role = CreditRole.Actor, Order = 0 });
            }

            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = _hdItem, PositionSeconds = 60, LastPlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            person = actor.Id;
        }

        var client = Client();
        var search = await client.GetFromJsonAsync<JsonElement>(Api("/search?q=Film"));
        Assert.Single(search.GetProperty("titles").EnumerateArray());
        var genrePage = await client.GetFromJsonAsync<JsonElement>(Api("/genres/Mystery/items"));
        Assert.Equal(1, genrePage.GetProperty("total").GetInt32());
        var page = await client.GetFromJsonAsync<JsonElement>(Api($"/people/{person}"));
        Assert.Single(page.GetProperty("credits").EnumerateArray());

        var home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        var carryOn = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "continue");
        Assert.Single(carryOn.GetProperty("items").EnumerateArray());

        // Each library's own page still has it.
        foreach (var (library, item) in new[] { (_hdLibrary, _hdItem), (_uhdLibrary, _uhdItem) })
        {
            var own = await client.GetFromJsonAsync<JsonElement>(Api($"/libraries/{library}/items"));
            Assert.Equal(item, own.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        }
    }

    [Fact]
    public async Task Lists_StillShowATitle_WhenTheCopyKeptIsHidden()
    {
        // Whichever copy has the lower id, hide that one: the other must show.
        var hidden = string.CompareOrdinal(_uhdItem.ToString(), _hdItem.ToString()) < 0 ? _uhdLibrary : _hdLibrary;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var main = await db.Profiles.SingleAsync(p => p.Id == PlaybackState.DefaultUser);
            main.HiddenLibraryIds = [hidden];
            await db.SaveChangesAsync();
            Flexarr.Api.Profiles.LibraryVisibility.KidsSettingsChanged();
        }

        var search = await Client().GetFromJsonAsync<JsonElement>(Api("/search?q=Film"));
        Assert.Equal(hidden == _uhdLibrary ? _hdItem : _uhdItem, search.GetProperty("titles").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    /// <summary>A show in the HD library with seasons 1 and 2, and in the 4K library with season 1 and one more episode.</summary>
    private async Task<(Guid Hd, Guid Uhd, Guid S1E1)> SeedShowAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var hd = await db.Libraries.SingleAsync(l => l.Id == _hdLibrary);
        var uhd = await db.Libraries.SingleAsync(l => l.Id == _uhdLibrary);

        MediaItem Show(Library library) => new() { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show", TvdbId = "81189" };
        MediaItem Season(Library library, MediaItem show, int number) =>
            new() { LibraryId = library.Id, ParentId = show.Id, Kind = MediaKind.Season, Title = $"Season {number}", SortTitle = $"{number:D3}", SeasonNumber = number };
        MediaItem Episode(Library library, MediaItem season, int number) =>
            new() { LibraryId = library.Id, ParentId = season.Id, Kind = MediaKind.Episode, Title = $"Episode {number}", SortTitle = $"{season.SeasonNumber:D3}{number:D3}", SeasonNumber = season.SeasonNumber, EpisodeNumber = number };

        var hdShow = Show(hd);
        var uhdShow = Show(uhd);
        var hdOne = Season(hd, hdShow, 1);
        var hdTwo = Season(hd, hdShow, 2);
        var uhdOne = Season(uhd, uhdShow, 1);
        var e1 = Episode(hd, hdOne, 1);
        var episodes = new[] { e1, Episode(hd, hdOne, 2), Episode(hd, hdTwo, 1), Episode(uhd, uhdOne, 1), Episode(uhd, uhdOne, 3) };
        db.AddRange(hdShow, uhdShow, hdOne, hdTwo, uhdOne);
        db.AddRange(episodes);
        db.AddRange(episodes.Select(e => File(e, $"{e.LibraryId}-{e.SortTitle}.mkv", 1920, 1080)));
        await db.SaveChangesAsync();
        await TitleLinker.LinkAsync(db, default);
        return (hdShow.Id, uhdShow.Id, e1.Id);
    }

    [Fact]
    public async Task AShowPage_CombinesItsCopies_FromEither()
    {
        var (hd, uhd, _) = await SeedShowAsync();
        var client = Client();
        foreach (var show in new[] { hd, uhd })
        {
            var detail = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{show}?includeSeason=true&season=1"));
            Assert.Equal([1, 2], detail.GetProperty("children").EnumerateArray().Select(s => s.GetProperty("seasonNumber").GetInt32()).Order());
            Assert.Equal(2, detail.GetProperty("item").GetProperty("childCount").GetInt32());
            // Season 1 has episodes 1 and 2 in HD, 1 and 3 in 4K: three in all.
            Assert.Equal([1, 2, 3], detail.GetProperty("season").GetProperty("children").EnumerateArray().Select(e => e.GetProperty("episodeNumber").GetInt32()).Order());
            Assert.Equal(4, detail.GetProperty("item").GetProperty("userData").GetProperty("unwatchedCount").GetInt32());
        }
    }

    [Fact]
    public async Task MarkingAShowWatched_CoversEveryCopy_AndCountsOnce()
    {
        var (hd, uhd, _) = await SeedShowAsync();
        var client = Client();
        (await client.PostAsJsonAsync(Api($"/items/{uhd}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        foreach (var show in new[] { hd, uhd })
        {
            var detail = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{show}"));
            Assert.True(detail.GetProperty("item").GetProperty("userData").GetProperty("watched").GetBoolean());
            Assert.Equal(0, detail.GetProperty("item").GetProperty("userData").GetProperty("unwatchedCount").GetInt32());
        }
    }

    [Fact]
    public async Task NextUp_RunsAcrossCopies()
    {
        var (_, uhd, e1) = await SeedShowAsync();
        var client = Client();
        (await client.PostAsJsonAsync(Api($"/items/{e1}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        var next = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{uhd}?includeSeason=true"));
        Assert.Equal(2, next.GetProperty("nextUp").GetProperty("episodeNumber").GetInt32());

        // Continue Watching holds the show once, with that episode.
        var home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        var carryOn = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "continue").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, Assert.Single(carryOn).GetProperty("episodeNumber").GetInt32());
    }

    [Fact]
    public async Task AHiddenCopy_AddsNothingToAShow()
    {
        var (hd, _, _) = await SeedShowAsync();
        await HideFourKAsync();
        var detail = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{hd}?includeSeason=true&season=1"));
        Assert.Equal([1, 2], detail.GetProperty("season").GetProperty("children").EnumerateArray().Select(e => e.GetProperty("episodeNumber").GetInt32()).Order());
    }

    [Fact]
    public async Task TheWatchlist_KnowsATitleByAnyCopy_AndShowsOneThisProfileCanSee()
    {
        var client = Client();
        (await client.PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = _uhdItem })).EnsureSuccessStatusCode();
        var detail = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}"));
        Assert.True(detail.GetProperty("onWatchlist").GetBoolean());

        await HideFourKAsync();
        var list = await client.GetFromJsonAsync<JsonElement>(Api("/watchlist"));
        var entry = list.EnumerateArray().Single();
        Assert.Equal(_hdItem, entry.GetProperty("item").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Downloads_StartFromAnyCopy_AndNeverAHiddenOne()
    {
        var client = Client();
        double Original(JsonElement options) => options.EnumerateArray().Single(o => o.GetProperty("quality").GetString() == "Original").GetProperty("estimatedBytes").GetDouble();

        // Asked for on the HD copy's page, the original is the best version: the 4K one.
        var both = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}/download-options"));
        Assert.Equal(2160 * 1000, Original(both));

        await HideFourKAsync();
        var hdOnly = await client.GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}/download-options"));
        Assert.Equal(1080 * 1000, Original(hdOnly));
    }
}
