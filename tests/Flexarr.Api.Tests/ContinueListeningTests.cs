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

    private sealed record Seeded(string Profile, Guid Film, Guid Book, Guid Episode, Guid Podcast);

    private sealed record Row(string Title, List<Guid> Ids);

    /// <summary>
    /// A new profile part way through a film (played 22 October, midday), The Man Who Died Twice (22 October) and an
    /// episode of The History Hour (23 October), and finished with another episode.
    /// </summary>
    private async Task<(HttpClient Client, Seeded Seeded)> SeedProfileAsync()
    {
        var owner = Client();
        var created = await (await owner.PostAsJsonAsync(Api("profiles"), new { name = $"Listener {Guid.NewGuid():N}"[..20] })).Content.ReadFromJsonAsync<JsonElement>();
        var profileId = created.GetProperty("id").GetString()!;
        var token = (await (await owner.PostAsJsonAsync(Api($"profiles/{profileId}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        var books = await AudiobookApiTests.SeedAsync(factory.Services, profileId);
        using var scope = factory.Services.CreateScope();
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

        return (Client(token), new Seeded(profileId, film.Id, books.Books["The Man Who Died Twice"], started.Id, podcast.Id));
    }

    private static async Task<Dictionary<string, Row>> RowsAsync(HttpClient client, string path) =>
        Rows(await client.GetFromJsonAsync<JsonElement>(Api(path)));

    private static Dictionary<string, Row> Rows(JsonElement home) =>
        home.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!,
            r => new Row(r.GetProperty("title").GetString()!, [.. r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())]));

    private static Uri Api(string path) => new($"/api/v1/{path}", UriKind.Relative);

    private HttpClient Client(string? profileToken = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }
}
