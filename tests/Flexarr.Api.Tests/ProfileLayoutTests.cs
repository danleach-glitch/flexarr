using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
