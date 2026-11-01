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
