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
    public async Task Details_LeaveASpareCopy_OutOfTheVersions()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var spare = File(await db.MediaItems.SingleAsync(i => i.Id == _hdItem), "Film (2020) 1080p copy.mkv", 1920, 1080);
            spare.Bitrate = 2_000_000;
            db.Add(spare);
            await db.SaveChangesAsync();
        }
        var detail = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{_hdItem}"));
        Assert.Equal([_uhdFile, _hdFile], detail.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("fileId").GetGuid()));
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
    public async Task AShowsSpeed_IsKeptAcrossItsCopies()
    {
        var (hd, uhd, _) = await SeedShowAsync();
        var client = Client();
        (await client.PutAsJsonAsync(Api($"/items/{hd}/playback-speed"), new { speed = 1.5 })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.Equal(1.5, await Flexarr.Api.Playback.TitleVersions.ShowSpeedAsync(db, uhd, PlaybackState.DefaultUser, default));
        }

        (await client.PutAsJsonAsync(Api($"/items/{uhd}/playback-speed"), new { speed = 2.0 })).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.Equal(2.0, await Flexarr.Api.Playback.TitleVersions.ShowSpeedAsync(db, hd, PlaybackState.DefaultUser, default));
        }

        (await client.PutAsJsonAsync(Api($"/items/{hd}/playback-speed"), new { speed = 1.0 })).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.Null(await Flexarr.Api.Playback.TitleVersions.ShowSpeedAsync(db, uhd, PlaybackState.DefaultUser, default));
        }
    }

    [Fact]
    public async Task SubtitleTiming_StaysWithTheFile_WhicheverCopyOpensIt()
    {
        var client = Client();
        async Task<(string Session, double Delay)> Open(Guid item)
        {
            var info = await (await client.PostAsJsonAsync(Api("/playback"), new { itemId = item, profile = Big })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(_uhdFile, info.GetProperty("fileId").GetGuid());
            return (info.GetProperty("sessionId").GetString()!, info.GetProperty("subtitleDelaySeconds").GetDouble());
        }

        var (session, before) = await Open(_hdItem);
        Assert.Equal(0, before);
        (await client.PostAsJsonAsync(Api($"/playback/{session}/progress"), new { positionSeconds = 30 })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(Api($"/items/{_hdItem}/subtitle-delay"), new { seconds = 2.5 })).EnsureSuccessStatusCode();

        Assert.Equal(2.5, (await Open(_uhdItem)).Delay);
        Assert.Equal(2.5, (await Open(_hdItem)).Delay);
    }

    [Fact]
    public async Task FirstProgressReports_ThatRace_AllGetRecorded()
    {
        var client = Client();
        var sessions = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var info = await (await client.PostAsJsonAsync(Api("/playback"), new { itemId = i % 2 == 0 ? _hdItem : _uhdItem, profile = Big })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
            sessions.Add(info.GetProperty("sessionId").GetString()!);
        }

        var reports = await Task.WhenAll(sessions.Select(s => client.PostAsJsonAsync(Api($"/playback/{s}/progress"), new { positionSeconds = 300 })));
        Assert.All(reports, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var states = await db.PlaybackStates.IgnoreQueryFilters().Where(p => p.MediaItemId == _hdItem || p.MediaItemId == _uhdItem).ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, p => Assert.Equal(300, p.PositionSeconds));
    }

    [Fact]
    public async Task ATwoPartFileAndItsSplitEpisodes_ShareWhetherTheyreWatched()
    {
        Guid two, three, whole, show;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            MediaItem Show(Guid library) => new() { LibraryId = library, Kind = MediaKind.Series, Title = "Parts", SortTitle = "parts", TvdbId = "555" };
            MediaItem Season(Guid library, MediaItem parent) => new() { LibraryId = library, ParentId = parent.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "001", SeasonNumber = 1 };
            MediaItem Episode(Guid library, MediaItem season, int number, int? end = null) =>
                new() { LibraryId = library, ParentId = season.Id, Kind = MediaKind.Episode, Title = $"E{number}", SortTitle = $"001{number:D3}", SeasonNumber = 1, EpisodeNumber = number, EpisodeNumberEnd = end };
            var (hdShow, uhdShow) = (Show(_hdLibrary), Show(_uhdLibrary));
            var (hdSeason, uhdSeason) = (Season(_hdLibrary, hdShow), Season(_uhdLibrary, uhdShow));
            var (e2And3, e2, e3) = (Episode(_hdLibrary, hdSeason, 2, 3), Episode(_uhdLibrary, uhdSeason, 2), Episode(_uhdLibrary, uhdSeason, 3));
            db.AddRange(hdShow, uhdShow, hdSeason, uhdSeason, e2And3, e2, e3);
            await db.SaveChangesAsync();
            await TitleLinker.LinkAsync(db, default);
            (two, three, whole, show) = (e2.Id, e3.Id, e2And3.Id, hdShow.Id);
        }

        var client = Client();
        async Task<bool> Watched(Guid id) => (await client.GetFromJsonAsync<JsonElement>(Api($"/items/{id}"))).GetProperty("item").GetProperty("userData").GetProperty("watched").GetBoolean();
        async Task Mark(Guid id, bool watched) => (await client.PostAsJsonAsync(Api($"/items/{id}/watched"), new { watched })).EnsureSuccessStatusCode();

        await Mark(whole, true);
        Assert.True(await Watched(two));
        Assert.True(await Watched(three));
        Assert.True(await Watched(show));

        await Mark(three, false);
        Assert.False(await Watched(whole));
        Assert.True(await Watched(two));

        await Mark(three, true);
        Assert.True(await Watched(whole));
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

    [Fact]
    public async Task Downloads_CanBeCreatedWhenOnlyAnotherCopyCanBeRead()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.MediaFiles.IgnoreQueryFilters().Where(f => f.MediaItemId == _hdItem).ExecuteUpdateAsync(u => u.SetProperty(f => f.ProbeError, "unreadable"));
        }

        var created = await Client().PostAsJsonAsync(Api("/offline"), new { itemId = _hdItem, quality = "Original" });
        Assert.True(created.IsSuccessStatusCode, created.StatusCode.ToString());
    }

    [Fact]
    public async Task Downloads_NeverUseACopyOverTheAgeLimit_OrBlocked()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.AddRange(new Profile { Id = "kid", Name = "Kid", MaxRatingAge = 12 }, new Profile { Id = "adult", Name = "Adult" });
        var uhd = await db.MediaItems.IgnoreQueryFilters().FirstAsync(i => i.Id == _uhdItem);
        uhd.ContentRating = "18";
        await db.SaveChangesAsync();

        var overLimit = await Flexarr.Api.Downloads.DownloadSource.FilesAsync(db, _hdItem, "kid", default);
        Assert.Equal([_hdFile], overLimit.Select(f => f.Id));
        // A profile with no limit still gets both.
        Assert.Equal(2, (await Flexarr.Api.Downloads.DownloadSource.FilesAsync(db, _hdItem, "adult", default)).Count);

        uhd.ContentRating = "PG";
        uhd.KidsOverride = KidsOverride.Block;
        await db.SaveChangesAsync();

        var blocked = await Flexarr.Api.Downloads.DownloadSource.FilesAsync(db, _hdItem, "kid", default);
        Assert.Equal([_hdFile], blocked.Select(f => f.Id));
    }

    [Theory]
    [InlineData("Block")]
    [InlineData("Allow")]
    public async Task AParentsSay_CoversEveryCopy(string say)
    {
        var response = await Client().PutAsJsonAsync(Api($"/items/{_hdItem}/kids"), new { @override = say });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var says = await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == _hdItem || i.Id == _uhdItem).Select(i => i.KidsOverride).ToListAsync();
        Assert.All(says, s => Assert.Equal(Enum.Parse<KidsOverride>(say), s));
    }

    [Fact]
    public async Task RemovingFromContinueWatching_ForgetsThePlaceOnEveryCopy()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = _hdItem, PositionSeconds = 600, LastPlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"/home/continue/{_hdItem}"))).StatusCode);

        var home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        Assert.DoesNotContain(home.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetString() == "continue" && r.GetProperty("items").GetArrayLength() > 0);
        using var check = _factory.Services.CreateScope();
        var states = await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().PlaybackStates.AsNoTracking().ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Equal(0, state.PositionSeconds));
    }

    [Fact]
    public async Task ATitle_IsFoundByWhicheverCopyMatches()
    {
        Guid person;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var films = await db.MediaItems.Where(i => i.Kind == MediaKind.Movie).OrderBy(i => i.Id).Include(i => i.Genres).ToListAsync();
            // Only the copy that comes second in id order, the one a list would otherwise leave out.
            films[1].Title = "Amelie";
            films[1].Genres.Add(new Genre { Name = "Mystery" });
            var actor = new Person { Name = "Sam Example" };
            db.Add(new Credit { Person = actor, MediaItemId = films[1].Id, Role = CreditRole.Actor, Order = 0 });
            await db.SaveChangesAsync();
            person = actor.Id;
        }

        var client = Client();
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(Api("/search?q=Amelie"))).GetProperty("titles").EnumerateArray());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>(Api("/genres/Mystery/items"))).GetProperty("total").GetInt32());
        var people = (await client.GetFromJsonAsync<JsonElement>(Api("/search?q=Sam"))).GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(person, Assert.Single(people).GetProperty("id").GetGuid());

        using var check = _factory.Services.CreateScope();
        var rules = new Flexarr.Api.Collections.SmartRules(Genres: ["Mystery"]);
        Assert.Single(await rules.Query(check.ServiceProvider.GetRequiredService<FlexarrDbContext>(), PlaybackState.DefaultUser, DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task SmartRules_ReadEveryCopysEpisodes()
    {
        var (hd, _, _) = await SeedShowAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        async Task<bool> Lists(string watched) =>
            await new Flexarr.Api.Collections.SmartRules(Type: "series", Watched: watched).Query(db, PlaybackState.DefaultUser, DateTime.UtcNow).AnyAsync();

        // Everything one copy holds watched: the other copy's episodes are still to watch.
        var episodes = await db.MediaItems.Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == hd).Select(e => e.Id).ToListAsync();
        db.PlaybackStates.AddRange(episodes.Select(e => new PlaybackState { MediaItemId = e, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow }));
        await db.SaveChangesAsync();

        Assert.True(await Lists("unwatched"));
        Assert.False(await Lists("watched"));

        var rest = await db.MediaItems.Where(e => e.Kind == MediaKind.Episode && !db.PlaybackStates.Any(p => p.MediaItemId == e.Id)).Select(e => e.Id).ToListAsync();
        db.PlaybackStates.AddRange(rest.Select(e => new PlaybackState { MediaItemId = e, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow }));
        await db.SaveChangesAsync();

        Assert.False(await Lists("unwatched"));
        Assert.True(await Lists("watched"));
    }
}
