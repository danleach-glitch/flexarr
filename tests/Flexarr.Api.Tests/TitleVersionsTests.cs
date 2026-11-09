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
}
