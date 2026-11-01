using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Home and browse, and the menu, are each profile's own.</summary>
public sealed class ProfileLayoutTests : IDisposable
{
    // Its own server: these change the main profile's layout, which other tests' Home rows depend on.
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AProfilesLayout_IsItsOwn_AndOneThatHasntChosen_KeepsTheServers()
    {
        var owner = Client();
        var guest = Client(await NewProfileTokenAsync());

        var layout = await owner.GetFromJsonAsync<JsonElement>(Api("/settings/layout"));
        var home = layout.GetProperty("home").EnumerateArray()
            .Select(r => new { id = r.GetProperty("id").GetString(), enabled = r.GetProperty("id").GetString() != "upcoming" }).ToList();
        var put = await owner.PutAsJsonAsync(Api("/settings/layout"), new { home, movies = Array.Empty<object>(), shows = Array.Empty<object>(), combineLibraries = false });
        put.EnsureSuccessStatusCode();

        Assert.False(await EnabledAsync(owner, "upcoming"));
        Assert.True(await EnabledAsync(guest, "upcoming"));
        Assert.False((await owner.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("combineLibraries").GetBoolean());
        Assert.True((await guest.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("combineLibraries").GetBoolean());
    }

    [Fact]
    public async Task AnyProfile_ChoosesItsOwnLayout()
    {
        var guest = Client(await NewProfileTokenAsync());

        var put = await guest.PutAsJsonAsync(Api("/settings/layout"), new { home = new[] { new { id = "gems", enabled = false } }, movies = Array.Empty<object>(), shows = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(await EnabledAsync(guest, "gems"));
        Assert.True(await EnabledAsync(Client(), "gems"));
    }

    [Fact]
    public async Task HiddenNavigation_IsSavedOnTheProfile_OnlyFromTheKnownItems()
    {
        var token = await NewProfileTokenAsync();
        var guest = Client(token);
        var id = (await guest.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().First(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetString();

        var saved = await guest.PutAsJsonAsync(Api($"/profiles/{id}/navigation"), new { hidden = new[] { "playlists", "discover", "playlists" } });
        var unknown = await guest.PutAsJsonAsync(Api($"/profiles/{id}/navigation"), new { hidden = new[] { "settings" } });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["playlists", "discover"], (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hiddenNavigation").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var owner = (await Client().GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().First(p => p.GetProperty("id").GetString() == "owner");
        Assert.Empty(owner.GetProperty("hiddenNavigation").EnumerateArray());
    }

    [Fact]
    public async Task NewPodcasts_IsAHomeRow_AfterContinueWatching_ThatCanBeSwitchedOff()
    {
        await AddLibraryAsync(LibraryType.Podcasts);
        // A film, so Home has rows of its own for New podcasts to go before.
        var films = await AddLibraryAsync(LibraryType.Movies);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Add(new Flexarr.Core.Media.MediaItem { LibraryId = films, Kind = Flexarr.Core.Media.MediaKind.Movie, Title = "Film", SortTitle = "film", CommunityRating = 8 });
            await db.SaveChangesAsync();
        }

        var client = Client(await NewProfileTokenAsync());

        var home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        var ids = (await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("home").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.True(home.GetProperty("placesPodcasts").GetBoolean());
        Assert.Contains(home.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetString() == "podcasts-new");
        Assert.Equal(ids.IndexOf("continue") + 1, ids.IndexOf("podcasts-new"));
        // With nothing saved, Home puts it where Settings lists it: first here, as there's nothing to continue.
        var rows = home.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.True(rows.Count > 1);
        Assert.Equal("podcasts-new", rows[0]);

        (await client.PutAsJsonAsync(Api("/settings/layout"), new { home = new[] { new { id = "podcasts-new", enabled = false } }, movies = Array.Empty<object>(), shows = Array.Empty<object>() })).EnsureSuccessStatusCode();

        home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        Assert.DoesNotContain(home.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetString() == "podcasts-new");
    }

    [Fact]
    public async Task AudiobookShelves_FollowTheProfilesOrder_AndLeaveOutSwitchedOffOnes()
    {
        var books = await AddLibraryAsync(LibraryType.Audiobooks);
        var client = Client(await NewProfileTokenAsync());
        var before = await client.GetFromJsonAsync<JsonElement>(Api($"/libraries/{books}/book-shelves"));
        Assert.Equal(["continue", "next", "recent", "series", "authors", "finished"], before.GetProperty("order").EnumerateArray().Select(e => e.GetString()));

        var layout = await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"));
        var home = layout.GetProperty("home").EnumerateArray().Select(r => new { id = r.GetProperty("id").GetString(), enabled = true });
        (await client.PutAsJsonAsync(Api("/settings/layout"), new
        {
            home,
            movies = Array.Empty<object>(),
            shows = Array.Empty<object>(),
            audiobooks = new[] { new { id = "series", enabled = true }, new { id = "finished", enabled = false } },
        })).EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>(Api($"/libraries/{books}/book-shelves"));
        Assert.Equal(["series", "continue", "next", "recent", "authors"], after.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
        // A save without them (an older web app) leaves them as they were.
        (await client.PutAsJsonAsync(Api("/settings/layout"), new { home, movies = Array.Empty<object>(), shows = Array.Empty<object>() })).EnsureSuccessStatusCode();
        Assert.Equal("series", (await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("audiobooks")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task MusicRows_AreOfferedOnlyWithAMusicLibrary()
    {
        var client = Client(await NewProfileTokenAsync());
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("music").ValueKind);

        await AddLibraryAsync(LibraryType.Music);

        var music = (await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("music").EnumerateArray().Select(r => r.GetProperty("id").GetString());
        Assert.Equal(["music-playlists", "music-mixes", "music-recent", "music-top", "music-artists", "recent"], music);
    }

    [Fact]
    public async Task HomeAndBrowse_OffersOnlyTheLibrariesTheProfileCanSee()
    {
        var shown = await AddLibraryAsync(LibraryType.Movies);
        var secret = await AddLibraryAsync(LibraryType.Movies);
        await AddLibraryAsync(LibraryType.Music);
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Guest {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var music = (await Client().GetFromJsonAsync<JsonElement>(Api("/libraries"))).EnumerateArray().Where(l => l.GetProperty("type").GetString() == "Music").Select(l => l.GetProperty("id").GetGuid());
        (await Client().PutAsJsonAsync(Api($"/profiles/{id}"), new { name = created.GetProperty("name").GetString(), hiddenLibraryIds = new[] { secret }.Concat(music) })).EnsureSuccessStatusCode();
        var selected = await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { });
        var guest = Client((await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        (await guest.PutAsJsonAsync(Api("/settings/layout"), new { home = Array.Empty<object>(), movies = Array.Empty<object>(), shows = Array.Empty<object>(), combineLibraries = false })).EnsureSuccessStatusCode();

        var layout = await guest.GetFromJsonAsync<JsonElement>(Api("/settings/layout"));

        var text = layout.GetRawText();
        Assert.Contains($"recent-{shown}", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret.ToString(), text, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, layout.GetProperty("music").ValueKind);
        Assert.False(layout.GetProperty("combinable").GetBoolean());
    }

    [Fact]
    public async Task ALayoutSave_IsChecked_SoItCantBreakHome()
    {
        var client = Client(await NewProfileTokenAsync());
        var none = Array.Empty<object>();

        var missing = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = (object?)null, movies = none, shows = none });
        var noId = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = new[] { new { id = (string?)null, enabled = false } }, movies = none, shows = none });
        var tooMany = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = Enumerable.Range(0, 201).Select(i => new { id = $"row-{i}", enabled = true }), movies = none, shows = none });
        var tooLong = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = none, movies = none, shows = none, music = new[] { new { id = new string('x', 129), enabled = true } } });

        Assert.All([missing, noId, tooMany, tooLong], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api("/home"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api("/settings/layout"))).StatusCode);
    }

    [Fact]
    public async Task ANewProfile_StartsWithAShorterHome_AndEveryBrowseRowOn()
    {
        await AddLibraryAsync(LibraryType.Music);
        var client = Client(await NewProfileTokenAsync());

        var layout = await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"));
        var home = layout.GetProperty("home").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("enabled").GetBoolean());

        Assert.True(home["continue"]);
        Assert.True(home["music-mixes"]);
        Assert.False(home["music-playlists"]);
        Assert.False(home["music-top"]);
        Assert.False(home["gems"]);
        Assert.All(layout.GetProperty("movies").EnumerateArray(), r => Assert.True(r.GetProperty("enabled").GetBoolean()));
        // The main profile keeps what it had.
        var owner = (await Client().GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("home").EnumerateArray();
        Assert.All(owner, r => Assert.True(r.GetProperty("enabled").GetBoolean()));
    }

    [Fact]
    public async Task AKidsProfile_HasTheStandardHomeAndMenu_AndCantChangeThem()
    {
        await _factory.SetMainPinAsync("2580");
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Playroom TV" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var tv = _factory.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        (await tv.PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).EnsureSuccessStatusCode();
        var none = Array.Empty<object>();

        var layout = await tv.PutAsJsonAsync(Api("/settings/layout"), new { home = new[] { new { id = "upcoming", enabled = false } }, movies = none, shows = none });
        var navigation = await Client().PutAsJsonAsync(Api($"/profiles/{id}/navigation"), new { hidden = new[] { "discover" } });

        Assert.Equal(HttpStatusCode.Forbidden, layout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, navigation.StatusCode);
        Assert.Empty(created.GetProperty("hiddenNavigation").EnumerateArray());
    }

    [Fact]
    public async Task OnlyTheMainProfile_SetsAnotherProfilesMenu()
    {
        await _factory.SetMainPinAsync("2580");
        var a = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"A {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"B {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Lounge TV" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var device = _factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Api-Key", token);
        (await device.PostAsJsonAsync(Api($"/profiles/{a.GetProperty("id").GetString()}/select"), new { })).EnsureSuccessStatusCode();

        var own = await device.PutAsJsonAsync(Api($"/profiles/{a.GetProperty("id").GetString()}/navigation"), new { hidden = new[] { "discover" } });
        var other = await device.PutAsJsonAsync(Api($"/profiles/{b.GetProperty("id").GetString()}/navigation"), new { hidden = new[] { "discover" } });
        var main = await device.PutAsJsonAsync(Api("/profiles/owner/navigation"), new { hidden = new[] { "discover" } });

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, main.StatusCode);
    }

    private async Task<Guid> AddLibraryAsync(LibraryType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"{type} {Guid.NewGuid():N}", Type = type };
        db.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    private static async Task<bool> EnabledAsync(HttpClient client, string row) =>
        (await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"))).GetProperty("home").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == row).GetProperty("enabled").GetBoolean();

    private HttpClient Client(string? profileToken = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private async Task<string> NewProfileTokenAsync()
    {
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Guest {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var selected = await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { });
        return (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }
}
