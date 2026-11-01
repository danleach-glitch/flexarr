using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Podcasts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Audiobooks and podcast episodes in progress in Home's Continue row, for apps that ask and profiles that want them.</summary>
public class ContinueListeningTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task WithoutListening_HomeIsAsBefore()
    {
        var (client, seeded) = await SeedProfileAsync();

        var rows = await RowsAsync(client, "home");

        Assert.Equal("Continue watching", rows["continue"].Title);
        Assert.Equal([seeded.Film], rows["continue"].Ids);
        Assert.Equal([seeded.Book], rows[BookRows.ContinueRowId].Ids);
    }

    [Fact]
    public async Task WithListening_BooksAndEpisodesJoinTheRow_ByWhenLastPlayed()
    {
        var (client, seeded) = await SeedProfileAsync();

        var home = await client.GetFromJsonAsync<JsonElement>(Api("home?listening=books,podcasts"));
        var rows = Rows(home);

        Assert.Equal("Continue watching or listening", rows["continue"].Title);
        Assert.Equal([seeded.Episode, seeded.Film, seeded.Book], rows["continue"].Ids);
        Assert.False(rows.ContainsKey(BookRows.ContinueRowId));
        // The hero still resumes a film or show.
        Assert.Equal(seeded.Film, home.GetProperty("resume").GetProperty("id").GetGuid());

        var items = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "continue").GetProperty("items").EnumerateArray().ToList();
        var episode = items[0];
        Assert.Equal("PodcastEpisode", episode.GetProperty("kind").GetString());
        Assert.Equal("The History Hour", episode.GetProperty("seriesTitle").GetString());
        Assert.Equal(seeded.Podcast, episode.GetProperty("seriesId").GetGuid());
        Assert.Equal(seeded.Podcast, episode.GetProperty("parentId").GetGuid());
        Assert.NotNull(episode.GetProperty("artwork").GetProperty("poster").GetString()); // the podcast's
        Assert.Equal(2400, episode.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(600, episode.GetProperty("userData").GetProperty("positionSeconds").GetDouble());

        var book = items[2];
        Assert.Equal("Audiobook", book.GetProperty("kind").GetString());
        Assert.Equal("Richard Osman", book.GetProperty("book").GetProperty("authors").GetString());
        Assert.Equal(1234, book.GetProperty("userData").GetProperty("positionSeconds").GetDouble());
        Assert.Equal(38000, book.GetProperty("durationSeconds").GetDouble(), 0.001);
    }

    [Fact]
    public async Task KindsNotAskedFor_AreLeftOut()
    {
        var (client, seeded) = await SeedProfileAsync();

        var books = await RowsAsync(client, "home?listening=books,cassettes");
        Assert.Equal("Continue watching or listening", books["continue"].Title);
        Assert.Equal([seeded.Film, seeded.Book], books["continue"].Ids);
        Assert.False(books.ContainsKey(BookRows.ContinueRowId));

        var podcasts = await RowsAsync(client, "home?listening=podcasts");
        Assert.Equal([seeded.Episode, seeded.Film], podcasts["continue"].Ids);
        // Books aren't in the row, so they keep their own.
        Assert.Equal([seeded.Book], podcasts[BookRows.ContinueRowId].Ids);

        var nothing = await RowsAsync(client, "home?listening=cassettes");
        Assert.Equal("Continue watching", nothing["continue"].Title);
        Assert.Equal([seeded.Film], nothing["continue"].Ids);
    }

    [Fact]
    public async Task AHiddenAudiobooksLibrary_KeepsItsBooksOutOfTheRow_AndTheyCantBeRemoved()
    {
        var (client, seeded) = await SeedProfileAsync();
        (await Client(factory).PutAsJsonAsync(Api($"profiles/{seeded.Profile}"), new { name = $"Hidden {Guid.NewGuid():N}"[..20], hiddenLibraryIds = new[] { seeded.BookLibrary } }))
            .EnsureSuccessStatusCode();

        var rows = await RowsAsync(client, "home?listening=books,podcasts");

        Assert.DoesNotContain(rows.Values, r => r.Ids.Contains(seeded.Book));
        Assert.Equal([seeded.Episode, seeded.Film], rows["continue"].Ids);
        Assert.False(rows.ContainsKey(BookRows.ContinueRowId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Api($"home/continue/{seeded.Book}"))).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var state = await db.PlaybackStates.AsNoTracking().SingleAsync(p => p.UserId == seeded.Profile && p.MediaItemId == seeded.Book);
        Assert.Equal(1234, state.PositionSeconds); // its place untouched
    }

    [Fact]
    public async Task AnotherProfilesListening_IsntInTheRow()
    {
        var (_, seeded) = await SeedProfileAsync();
        var (other, _) = await SeedProfileAsync();
        // The other profile has its own places in the same kinds of things, none in these.
        var rows = await RowsAsync(other, "home?listening=books,podcasts");

        Assert.DoesNotContain(seeded.Book, rows["continue"].Ids);
        Assert.DoesNotContain(seeded.Episode, rows["continue"].Ids);
        Assert.DoesNotContain(seeded.Film, rows["continue"].Ids);
    }

    [Fact]
    public async Task SettingsHome_SaysContinueWatchingCoversListening()
    {
        await SeedProfileAsync(); // an audiobooks library, so its rows are offered

        var layout = await Client(factory).GetFromJsonAsync<JsonElement>(Api("settings/layout"));
        var labels = layout.GetProperty("home").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("label").GetString());

        Assert.Equal("Continue watching (and listening)", labels["continue"]);
        Assert.Equal("Continue listening (when kept apart)", labels[BookRows.ContinueRowId]);
    }

    [Fact]
    public async Task WithNothingToListenTo_TheRowIsContinueWatching()
    {
        var (client, seeded) = await SeedProfileAsync();
        await client.DeleteAsync(Api($"home/continue/{seeded.Book}"));
        await client.DeleteAsync(Api($"home/continue/{seeded.Episode}"));

        var rows = await RowsAsync(client, "home?listening=books,podcasts");

        Assert.Equal("Continue watching", rows["continue"].Title);
        Assert.Equal([seeded.Film], rows["continue"].Ids);
    }

    [Fact]
    public async Task TheSwitchOff_FilmsAndShowsOnly_AndContinueListeningIsBack()
    {
        var (client, seeded) = await SeedProfileAsync();

        var preferences = await client.GetFromJsonAsync<JsonElement>(Api("me/playback-preferences"));
        Assert.True(preferences.GetProperty("listeningInContinue").GetBoolean());
        var saved = await client.PutAsJsonAsync(Api("me/playback-preferences"), new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, listeningInContinue = false });
        saved.EnsureSuccessStatusCode();
        Assert.False((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("listeningInContinue").GetBoolean());

        var rows = await RowsAsync(client, "home?listening=books,podcasts");
        Assert.Equal("Continue watching", rows["continue"].Title);
        Assert.Equal([seeded.Film], rows["continue"].Ids);
        Assert.Equal([seeded.Book], rows[BookRows.ContinueRowId].Ids);
    }

    [Fact]
    public async Task ASaveWithoutTheSwitch_LeavesIt()
    {
        var (client, _) = await SeedProfileAsync();
        (await client.PutAsJsonAsync(Api("me/playback-preferences"), new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, listeningInContinue = false }))
            .EnsureSuccessStatusCode();

        // An older app saving its toggles, and one sending null.
        var older = await client.PutAsJsonAsync(Api("me/playback-preferences"), new { autoPlayNext = false, skipIntros = true, skipRecaps = false, skipCredits = false });
        older.EnsureSuccessStatusCode();
        Assert.False((await older.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("listeningInContinue").GetBoolean());
        (await client.PutAsJsonAsync(Api("me/playback-preferences"), new { autoPlayNext = false, skipIntros = true, skipRecaps = false, skipCredits = false, listeningInContinue = (bool?)null }))
            .EnsureSuccessStatusCode();

        var preferences = await client.GetFromJsonAsync<JsonElement>(Api("me/playback-preferences"));
        Assert.False(preferences.GetProperty("listeningInContinue").GetBoolean());
        Assert.False(preferences.GetProperty("autoPlayNext").GetBoolean());
    }

    [Fact]
    public async Task RemovingABookOrEpisode_ForgetsThePlace_WithoutFinishingIt()
    {
        var (client, seeded) = await SeedProfileAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"home/continue/{seeded.Book}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"home/continue/{seeded.Episode}"))).StatusCode);

        var rows = await RowsAsync(client, "home?listening=books,podcasts");
        Assert.Equal([seeded.Film], rows["continue"].Ids);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        foreach (var id in new[] { seeded.Book, seeded.Episode })
        {
            var state = await db.PlaybackStates.AsNoTracking().SingleAsync(p => p.UserId == seeded.Profile && p.MediaItemId == id);
            Assert.Equal((0.0, false, (DateTime?)null), (state.PositionSeconds, state.Watched, state.FinishedAt));
        }
    }

    [Fact]
    public async Task APlayersSave_LeavesTheSwitchAlone()
    {
        var (client, seeded) = await SeedProfileAsync();
        (await client.PutAsJsonAsync(Api("me/playback-preferences"), new { autoPlayNext = true, skipIntros = false, skipRecaps = false, skipCredits = false, listeningInContinue = false }))
            .EnsureSuccessStatusCode();

        // A podcast player gets the preferences without the switch, and saves them back as it got them.
        var started = await client.PostAsJsonAsync(Api("playback"), new { itemId = seeded.Episode, profile = new { } });
        started.EnsureSuccessStatusCode();
        var info = await started.Content.ReadFromJsonAsync<JsonElement>();
        var given = info.GetProperty("preferences");
        Assert.True(!given.TryGetProperty("listeningInContinue", out var listening) || listening.ValueKind == JsonValueKind.Null);
        (await client.PutAsJsonAsync(Api("me/playback-preferences"), given)).EnsureSuccessStatusCode();
        await client.DeleteAsync(Api($"playback/{info.GetProperty("sessionId").GetString()}"));

        var preferences = await client.GetFromJsonAsync<JsonElement>(Api("me/playback-preferences"));
        Assert.False(preferences.GetProperty("listeningInContinue").GetBoolean());
    }

    [Fact]
    public async Task ARemovedBookOrEpisode_StaysOut_WhenAnOlderPlaceArrives()
    {
        var (client, seeded) = await SeedProfileAsync();
        var before = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"home/continue/{seeded.Book}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"home/continue/{seeded.Episode}"))).StatusCode);

        // A player's last save, reached before the removal and arriving after it; and listening done offline before it.
        (await client.PutAsJsonAsync(Api($"items/{seeded.Book}/progress"), new { positionSeconds = 1500, recordedAt = before })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Api($"items/{seeded.Book}/offline-progress"), new { positionSeconds = 1600, finished = false, playedAt = before })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Api($"items/{seeded.Episode}/offline-progress"), new { positionSeconds = 900, finished = false, playedAt = before })).EnsureSuccessStatusCode();

        var rows = await RowsAsync(client, "home?listening=books,podcasts");
        Assert.Equal([seeded.Film], rows["continue"].Ids);

        // Listening after the removal brings it back.
        (await client.PutAsJsonAsync(Api($"items/{seeded.Book}/progress"), new { positionSeconds = 1700 })).EnsureSuccessStatusCode();
        rows = await RowsAsync(client, "home?listening=books,podcasts");
        Assert.Equal([seeded.Book, seeded.Film], rows["continue"].Ids);
    }

    internal sealed record Seeded(string Profile, Guid Film, Guid Book, Guid Episode, Guid Podcast, Guid BookLibrary);

    internal sealed record Row(string Title, List<Guid> Ids);

    private Task<(HttpClient Client, Seeded Seeded)> SeedProfileAsync() => SeedProfileAsync(factory);

    /// <summary>
    /// A new profile part way through a film (played 22 October, midday), The Man Who Died Twice (22 October) and an
    /// episode of The History Hour (23 October), and finished with another episode.
    /// </summary>
    internal static async Task<(HttpClient Client, Seeded Seeded)> SeedProfileAsync(FlexarrApiFactory server)
    {
        var owner = Client(server);
        var created = await (await owner.PostAsJsonAsync(Api("profiles"), new { name = $"Listener {Guid.NewGuid():N}"[..20] })).Content.ReadFromJsonAsync<JsonElement>();
        var profileId = created.GetProperty("id").GetString()!;
        await server.ShowEveryHomeRowAsync(profileId);
        var token = (await (await owner.PostAsJsonAsync(Api($"profiles/{profileId}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        return (Client(server, token), await SeedAsync(server, profileId));
    }

    /// <summary>What <see cref="SeedProfileAsync(FlexarrApiFactory)"/> seeds, for <paramref name="profileId"/>.</summary>
    internal static async Task<Seeded> SeedAsync(FlexarrApiFactory server, string profileId)
    {
        var books = await AudiobookApiTests.SeedAsync(server.Services, profileId);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        db.Libraries.Add(films);
        var film = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Paddington", SortTitle = "paddington" };
        db.MediaItems.Add(film);

        var podcastLibrary = await db.Libraries.SingleAsync(l => l.Type == LibraryType.Podcasts);
        var podcast = new MediaItem
        {
            LibraryId = podcastLibrary.Id, Kind = MediaKind.Podcast, Title = "The History Hour", SortTitle = "history hour", PosterUrl = "https://cdn.example.com/show.jpg",
            Feed = new PodcastFeed { Url = $"https://feeds.example.com/{Guid.NewGuid():N}.xml" },
        };
        db.MediaItems.Add(podcast);
        MediaItem Episode(string title) => new()
        {
            LibraryId = podcastLibrary.Id, Kind = MediaKind.PodcastEpisode, Title = title, SortTitle = title.ToLowerInvariant(), Parent = podcast,
            Enclosure = new PodcastEpisode { FeedId = podcast.Id, EpisodeGuid = title, AudioUrl = $"https://cdn.example.com/{Guid.NewGuid():N}.mp3", DurationSeconds = 2400 },
        };
        var started = Episode("Episode 2");
        var finished = Episode("Episode 1");
        db.MediaItems.AddRange(started, finished);
        db.PlaybackStates.AddRange(
            new PlaybackState { UserId = profileId, MediaItemId = film.Id, PositionSeconds = 300, LastPlayedAt = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc) },
            new PlaybackState { UserId = profileId, MediaItemId = started.Id, PositionSeconds = 600, LastPlayedAt = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc) },
            new PlaybackState { UserId = profileId, MediaItemId = finished.Id, Watched = true, LastPlayedAt = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        return new Seeded(profileId, film.Id, books.Books["The Man Who Died Twice"], started.Id, podcast.Id, books.Library);
    }

    internal static async Task<Dictionary<string, Row>> RowsAsync(HttpClient client, string path) =>
        Rows(await client.GetFromJsonAsync<JsonElement>(Api(path)));

    private static Dictionary<string, Row> Rows(JsonElement home) =>
        home.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!,
            r => new Row(r.GetProperty("title").GetString()!, [.. r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())]));

    internal static Uri Api(string path) => new($"/api/v1/{path}", UriKind.Relative);

    internal static HttpClient Client(FlexarrApiFactory server, string? profileToken = null)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }
}

/// <summary>The Continue row where the household's settings change (Home's layout, a main PIN): each on a server of its own.</summary>
public sealed class ContinueListeningLayoutTests
{
    [Fact]
    public async Task ContinueSwitchedOffOnHome_BooksKeepTheirOwnRow()
    {
        using var server = new FlexarrApiFactory();
        var (client, seeded) = await ContinueListeningTests.SeedProfileAsync(server);
        await SwitchOffAsync(client, "continue");

        var rows = await ContinueListeningTests.RowsAsync(client, "home?listening=books,podcasts");

        Assert.False(rows.ContainsKey("continue"));
        Assert.Equal([seeded.Book], rows[BookRows.ContinueRowId].Ids);
    }

    [Fact]
    public async Task AKidsProfile_GetsNoBooksOrEpisodesInTheRow_AndCantRemoveThem()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var admin = ContinueListeningTests.Client(server);
        var created = await admin.PostAsJsonAsync(ContinueListeningTests.Api("profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var kidId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var token = (await (await admin.PostAsJsonAsync(ContinueListeningTests.Api("session/device"), new { deviceName = "Playroom TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(ContinueListeningTests.Api($"profiles/{kidId}/select"), new { })).StatusCode);
        // Part way through a book and an episode (played on another app before the limit, say).
        var seeded = await ContinueListeningTests.SeedAsync(server, kidId);

        var rows = await ContinueListeningTests.RowsAsync(tv, "home?listening=books,podcasts");

        Assert.DoesNotContain(rows.Values, r => r.Ids.Contains(seeded.Book) || r.Ids.Contains(seeded.Episode));
        Assert.False(rows.ContainsKey(BookRows.ContinueRowId));
        Assert.True(!rows.TryGetValue("continue", out var row) || row.Title == "Continue watching");
        Assert.Equal(HttpStatusCode.NotFound, (await tv.DeleteAsync(ContinueListeningTests.Api($"home/continue/{seeded.Book}"))).StatusCode);
    }

    /// <summary>Saves the profile's Home layout with <paramref name="rowId"/> off.</summary>
    internal static async Task SwitchOffAsync(HttpClient client, string rowId)
    {
        var saved = new[] { new { id = rowId, enabled = false } };
        (await client.PutAsJsonAsync(ContinueListeningTests.Api("settings/layout"),
            new { home = saved, movies = Array.Empty<object>(), shows = Array.Empty<object>() })).EnsureSuccessStatusCode();
    }
}
