using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Events;
using Flexarr.Api.Trakt;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

/// <summary>
/// Trakt: connecting by device code, tokens kept fresh, scrobbling, the outbox, the history import with its preview,
/// the two-way sync (watched wins) and the watchlist both ways. Trakt and the relay are a <see cref="FakeTrakt"/>.
/// </summary>
public sealed class TraktTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeTrakt _trakt = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        TraktClient.ForgetClientId();
        TraktAccounts.ForgetFailures();
        _server = _trakt.Serve(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _trakt.Dispose();
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task SeedAsync(Action<FlexarrDbContext> seed)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> InDbAsync<T>(Func<FlexarrDbContext, Task<T>> query)
    {
        using var scope = _server.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>());
    }

    private async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _server.Services.CreateScope();
        await work(scope.ServiceProvider);
    }

    private const string Owner = PlaybackState.DefaultUser;

    private Task ConnectAsync(string profileId = Owner, DateTime? expires = null, string slug = "dan", DateTime? importedAt = null) => SeedAsync(db =>
        db.TraktConnections.Add(new TraktConnection
        {
            ProfileId = profileId, AccessToken = "access-token-0123456789", RefreshToken = "refresh-token-0123456789", UserSlug = slug, Username = "Dan",
            ExpiresAt = expires ?? DateTime.UtcNow.AddHours(20), ImportedAt = importedAt, HistoryCursor = importedAt,
        }));

    private static readonly Library Films = new() { Name = "Films", Type = LibraryType.Movies };

    private static MediaItem Film(string title, string tmdb, string? imdb = null) =>
        new() { LibraryId = Films.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant(), Year = 2021, TmdbId = tmdb, ImdbId = imdb };

    private static (Library Library, MediaItem Show, MediaItem Episode, MediaItem[] All) Show(string tvdb)
    {
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Severance", SortTitle = "severance", TvdbId = tvdb };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, Parent = show };
        var episode = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Good News About Hell", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1, Parent = season };
        return (library, show, episode, [show, season, episode]);
    }

    private async Task<JsonElement> StatusAsync() => await Client().GetFromJsonAsync<JsonElement>(Api("/trakt/status"));

    // ---- Availability and connecting ----

    [Fact]
    public async Task WithoutCredentials_TraktIsNotOffered()
    {
        TraktClient.ForgetClientId();
        _trakt.Set("GET /api/trakt", """{"reason":"NotConfigured"}""", HttpStatusCode.ServiceUnavailable);

        var status = await StatusAsync();
        var connect = await Client().PostAsync(Api("/trakt/connect"), null);

        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, connect.StatusCode);
    }

    [Fact]
    public async Task TheAdminsOwnApp_GoesStraightToTrakt_WithItsSecret()
    {
        TraktClient.ForgetClientId();
        _trakt.Set("GET /api/trakt", "{}", HttpStatusCode.ServiceUnavailable);
        _server.Services.GetRequiredService<TraktConnector>().MinimumInterval = TimeSpan.FromMilliseconds(20);
        _trakt.Set("POST /oauth/device/token", FakeTrakt.Token());

        var saved = await Client().PutAsJsonAsync(Api("/settings"), new { traktApp = new { clientId = "my-own-id", clientSecret = "my-own-secret" } });
        var settings = await Client().GetFromJsonAsync<JsonElement>(Api("/settings"));
        await Client().PostAsync(Api("/trakt/connect"), null);
        var status = await WaitForAsync(s => s.GetProperty("state").GetString() == "connected");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("my-own-id", settings.GetProperty("traktApp").GetProperty("clientId").GetString());
        Assert.True(settings.GetProperty("traktApp").GetProperty("hasSecret").GetBoolean());
        Assert.DoesNotContain("my-own-secret", settings.GetRawText(), StringComparison.Ordinal);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Contains("my-own-secret", _trakt.Bodies("POST /oauth/device/token").First(), StringComparison.Ordinal);
        Assert.DoesNotContain(_trakt.Requests, r => r.Key == "POST /api/trakt");
    }

    [Fact]
    public async Task Connecting_ShowsACode_TheServerWaits_ThenItsConnected()
    {
        _server.Services.GetRequiredService<TraktConnector>().MinimumInterval = TimeSpan.FromMilliseconds(20);
        _trakt.Then("RELAY device", new FakeTrakt.Answer(HttpStatusCode.BadRequest), new FakeTrakt.Answer(HttpStatusCode.BadRequest),
            new FakeTrakt.Answer(HttpStatusCode.OK, FakeTrakt.Token()));

        var started = await Client().PostAsync(Api("/trakt/connect"), null);
        var code = await started.Content.ReadFromJsonAsync<JsonElement>();
        var status = await WaitForAsync(s => s.GetProperty("state").GetString() == "connected");

        Assert.Equal("ABCD1234", code.GetProperty("userCode").GetString());
        Assert.Equal("https://trakt.tv/activate", code.GetProperty("verificationUrl").GetString());
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal("Dan", status.GetProperty("username").GetString());
        Assert.Equal("connected", status.GetProperty("pending").GetProperty("state").GetString()!.ToLowerInvariant());
        // The secret never leaves the relay: the server only ever sent it the device code.
        Assert.All(_trakt.RelayBodies, b => Assert.DoesNotContain("secret", b, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("flexarr-id", JsonDocument.Parse(_trakt.Bodies("POST /oauth/device/code").Single()).RootElement.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task ATraktAccount_ConnectsToOneProfileOnly()
    {
        await SeedAsync(db => db.Profiles.Add(new Profile { Id = "sam", Name = "Sam" }));
        await ConnectAsync("sam", slug: "dan");
        _server.Services.GetRequiredService<TraktConnector>().MinimumInterval = TimeSpan.FromMilliseconds(20);
        _trakt.Set("RELAY device", FakeTrakt.Token());
        _trakt.Set("RELAY revoke", "{}");

        await Client().PostAsync(Api("/trakt/connect"), null);
        var status = await WaitForAsync(s => s.GetProperty("pending").ValueKind == JsonValueKind.Object && s.GetProperty("pending").GetProperty("state").GetString() != "Waiting");

        Assert.Equal("disconnected", status.GetProperty("state").GetString());
        Assert.Equal("InUse", status.GetProperty("pending").GetProperty("state").GetString());
        Assert.Equal(1, _trakt.Count("POST /api/trakt") - _trakt.RelayBodies.Count(b => b.Contains("device_token", StringComparison.Ordinal)));
    }

    private async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await StatusAsync();
            if (done(status))
            {
                return status;
            }

            await Task.Delay(30);
        }

        throw new TimeoutException("Trakt status never got there.");
    }

    [Fact]
    public async Task ACodeNobodyEnters_Expires()
    {
        _server.Services.GetRequiredService<TraktConnector>().MinimumInterval = TimeSpan.FromMilliseconds(20);
        _trakt.Then("RELAY device", new FakeTrakt.Answer(HttpStatusCode.BadRequest), new FakeTrakt.Answer(HttpStatusCode.Gone));

        await Client().PostAsync(Api("/trakt/connect"), null);
        var status = await WaitForAsync(s => s.GetProperty("pending").ValueKind == JsonValueKind.Object && s.GetProperty("pending").GetProperty("state").GetString() != "Waiting");

        Assert.Equal("Expired", status.GetProperty("pending").GetProperty("state").GetString());
        Assert.Equal("disconnected", status.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Disconnecting_RevokesTheToken_AndForgetsItsQueue()
    {
        await ConnectAsync();
        await SeedAsync(db => db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = "{}" }));
        _trakt.Set("RELAY revoke", "{}");

        var response = await Client().DeleteAsync(Api("/trakt"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(_trakt.RelayBodies, b => b.Contains("revoke", StringComparison.Ordinal));
        Assert.False(await InDbAsync(db => db.TraktConnections.AnyAsync()));
        Assert.False(await InDbAsync(db => db.TraktOutbox.AnyAsync()));
    }

    // ---- Tokens ----

    [Fact]
    public async Task ATokenNearItsEnd_IsRefreshed_ThroughTheRelay()
    {
        await ConnectAsync(expires: DateTime.UtcNow.AddMinutes(30));
        _trakt.Set("RELAY refresh", FakeTrakt.Token("new-access-token-01234", "new-refresh-token-0123"));

        string? token = null;
        await InScopeAsync(async s => token = await s.GetRequiredService<TraktAccounts>().TokenAsync(Owner, force: false, default));
        var saved = await InDbAsync(db => db.TraktConnections.SingleAsync());

        Assert.Equal("new-access-token-01234", token);
        Assert.Equal("new-refresh-token-0123", saved.RefreshToken);
        Assert.True(saved.ExpiresAt > DateTime.UtcNow.AddHours(23));
    }

    [Fact]
    public async Task ARefusedRefresh_AsksToSignInAgain()
    {
        await ConnectAsync(expires: DateTime.UtcNow.AddMinutes(30));
        _trakt.Set("RELAY refresh", """{"error":"invalid_grant"}""", HttpStatusCode.Unauthorized);

        string? token = "x";
        await InScopeAsync(async s => token = await s.GetRequiredService<TraktAccounts>().TokenAsync(Owner, force: false, default));
        var status = await StatusAsync();

        Assert.Null(token);
        Assert.Equal("needsSignIn", status.GetProperty("state").GetString());
    }

    // ---- Scrobbling ----

    private TraktScrobbler Scrobbler() =>
        new(_server.Services.GetRequiredService<PlaybackEvents>(), _server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TraktScrobbler>.Instance);

    private async Task<List<TraktOutboxEntry>> OutboxAsync() => await InDbAsync(db => db.TraktOutbox.OrderBy(o => o.Id).ToListAsync());

    [Fact]
    public async Task Playing_ScrobblesStartAndStop_OnlyWhenFlexarrCountsItWatched()
    {
        var film = Film("Dune", "438631", "tt1160419");
        await SeedAsync(db => db.AddRange(Films, film));
        await ConnectAsync();
        var scrobbler = Scrobbler();

        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FlexarrDbContext>();
            await scrobbler.HandleAsync(db, PlaybackEventKind.Started, "a", Owner, film.Id, MediaKind.Movie, 10, 1000, default);
            // The credits start at 85%: Flexarr counts that as watched, so Trakt is told it was.
            await scrobbler.HandleAsync(db, PlaybackEventKind.Credits, "a", Owner, film.Id, MediaKind.Movie, 850, 1000, default);
            await scrobbler.HandleAsync(db, PlaybackEventKind.Stopped, "a", Owner, film.Id, MediaKind.Movie, 870, 1000, default);
            // Another viewing, given up at 85% with no credits marker: a pause, so Trakt doesn't count it.
            await scrobbler.HandleAsync(db, PlaybackEventKind.Started, "b", Owner, film.Id, MediaKind.Movie, 0, 1000, default);
            await scrobbler.HandleAsync(db, PlaybackEventKind.Stopped, "b", Owner, film.Id, MediaKind.Movie, 850, 1000, default);
        });
        var queued = await OutboxAsync();

        Assert.Equal(["start", "stop", "start", "pause"], queued.Select(q => q.Action));
        var stop = JsonSerializer.Deserialize<TraktScrobbleBody>(queued[1].Payload, TraktJsonForTests.Options)!;
        Assert.True(stop.Progress >= 95);
        Assert.Equal(438631, stop.Movie!.Ids.Tmdb);
        Assert.Equal(85, JsonSerializer.Deserialize<TraktScrobbleBody>(queued[3].Payload, TraktJsonForTests.Options)!.Progress);
    }

    [Fact]
    public async Task AnEpisode_IsScrobbledByItsShowAndNumber()
    {
        var (library, _, episode, all) = Show("371980");
        await SeedAsync(db => { db.Libraries.Add(library); db.MediaItems.AddRange(all); });
        await ConnectAsync();

        await InScopeAsync(s => Scrobbler().HandleAsync(s.GetRequiredService<FlexarrDbContext>(), PlaybackEventKind.Started, "a", Owner, episode.Id, MediaKind.Episode, 10, 1000, default));
        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));

        var sent = JsonDocument.Parse(_trakt.Bodies("POST /scrobble/start").Single()).RootElement;
        Assert.Equal(371980, sent.GetProperty("show").GetProperty("ids").GetProperty("tvdb").GetInt32());
        Assert.Equal(1, sent.GetProperty("episode").GetProperty("season").GetInt32());
        Assert.Equal(1, sent.GetProperty("episode").GetProperty("number").GetInt32());
        Assert.Empty(await OutboxAsync());
    }

    [Fact]
    public async Task ProfilesNotConnected_ScrobbleNothing()
    {
        var film = Film("Dune", "438631");
        await SeedAsync(db => db.AddRange(Films, film));

        await InScopeAsync(s => Scrobbler().HandleAsync(s.GetRequiredService<FlexarrDbContext>(), PlaybackEventKind.Started, "a", Owner, film.Id, MediaKind.Movie, 10, 1000, default));

        Assert.Empty(await OutboxAsync());
    }

    // ---- The outbox ----

    [Fact]
    public async Task TheOutbox_RetriesWhileTraktIsDown_AndDropsWhatTraktRefuses()
    {
        await ConnectAsync();
        await SeedAsync(db =>
        {
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = """{"movies":[{"ids":{"tmdb":1}}]}""" });
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.WatchlistRemove, Payload = """{"movies":[{"ids":{"tmdb":2}}]}""" });
        });
        _trakt.Set("POST /sync/history", "{}", HttpStatusCode.ServiceUnavailable);
        _trakt.Set("POST /sync/watchlist/remove", "{}", HttpStatusCode.UnprocessableEntity);

        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));
        var left = await OutboxAsync();

        var waiting = Assert.Single(left);
        Assert.Equal(TraktOutboxKind.History, waiting.Kind);
        Assert.Equal(1, waiting.Attempts);
        Assert.True(waiting.NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task SlowDown_WaitsAsLongAsTraktSays_AndHoldsEverythingBack()
    {
        await ConnectAsync();
        await SeedAsync(db =>
        {
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = """{"movies":[{"ids":{"tmdb":1}}]}""" });
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = """{"movies":[{"ids":{"tmdb":2}}]}""" });
        });
        _trakt.Answers["POST /sync/history"] = new([new FakeTrakt.Answer((HttpStatusCode)429)]);

        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));
        var left = await OutboxAsync();

        // One try, then the rest wait their turn.
        Assert.Equal(1, _trakt.Count("POST /sync/history"));
        Assert.Equal(2, left.Count);
        Assert.True(left[0].NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task AStaleFinish_GoesToHistory_WithWhenItHappened()
    {
        await ConnectAsync();
        var at = DateTime.UtcNow.AddHours(-1);
        await SeedAsync(db => db.TraktOutbox.Add(new TraktOutboxEntry
        {
            ProfileId = Owner, Kind = TraktOutboxKind.Scrobble, Action = "stop", CreatedAt = at,
            Payload = """{"movie":{"ids":{"tmdb":438631}},"progress":96}""",
        }));

        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));
        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));

        Assert.Equal(0, _trakt.Count("POST /scrobble/stop"));
        var history = JsonDocument.Parse(_trakt.Bodies("POST /sync/history").Single()).RootElement.GetProperty("movies")[0];
        Assert.Equal(438631, history.GetProperty("ids").GetProperty("tmdb").GetInt32());
        Assert.True(Math.Abs((history.GetProperty("watched_at").GetDateTime().ToUniversalTime() - at).TotalSeconds) < 2);
    }

    [Fact]
    public async Task MarkingWatched_GoesToTraktsHistory_MarkingUnwatchedDoesnt()
    {
        var film = Film("Dune", "438631");
        await SeedAsync(db => db.AddRange(Films, film));
        await ConnectAsync();

        await Client().PostAsJsonAsync(Api($"/items/{film.Id}/watched"), new { watched = true });
        await Client().PostAsJsonAsync(Api($"/items/{film.Id}/watched"), new { watched = false });
        var queued = await OutboxAsync();

        var history = Assert.Single(queued);
        Assert.Equal(TraktOutboxKind.History, history.Kind);
        Assert.Contains("438631", history.Payload, StringComparison.Ordinal);
    }

    // ---- The merge rules ----

    [Fact]
    public async Task WatchedWins_ButAChangeHereSinceThePlay_Stands()
    {
        var film = Film("Dune", "438631");
        var unwatchedByHand = Film("Arrival", "329865");
        var watching = Film("Heat", "949");
        await SeedAsync(db =>
        {
            db.AddRange(Films, film, unwatchedByHand, watching);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = unwatchedByHand.Id, Watched = false, LastPlayedAt = DateTime.UtcNow });
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = watching.Id, Watched = false, PositionSeconds = 600, LastPlayedAt = DateTime.UtcNow });
        });
        var play = DateTime.UtcNow.AddDays(-1);

        await InScopeAsync(async s =>
        {
            var db = s.GetRequiredService<FlexarrDbContext>();
            foreach (var item in new[] { film, unwatchedByHand, watching })
            {
                TraktMerge.ApplyPlay(db, await db.PlaybackStates.FirstOrDefaultAsync(p => p.MediaItemId == item.Id), Owner, item.Id, play);
            }

            await db.SaveChangesAsync();
        });
        var states = await InDbAsync(db => db.PlaybackStates.ToDictionaryAsync(p => p.MediaItemId));

        Assert.True(states[film.Id].Watched);
        Assert.False(states[unwatchedByHand.Id].Watched);
        Assert.Equal(600, states[watching.Id].PositionSeconds);
    }

    // ---- Import ----

    [Fact]
    public async Task TheImport_PreviewsFirst_ThenBringsHistoryAcross_AndRemembersWhatIsntHere()
    {
        var dune = Film("Dune", "438631");
        var ownPlay = Film("Heat", "949");
        var (library, _, episode, all) = Show("371980");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune, ownPlay, library);
            db.MediaItems.AddRange(all);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = ownPlay.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddDays(-3) });
        });
        await ConnectAsync();
        _trakt.Set("GET /sync/watched/movies", """
            [{"plays":1,"last_watched_at":"2025-01-02T20:00:00.000Z","movie":{"title":"Dune","ids":{"tmdb":438631}}},
             {"plays":2,"last_watched_at":"2025-03-04T20:00:00.000Z","movie":{"title":"Arrival","ids":{"tmdb":329865,"imdb":"tt2543164"}}},
             {"plays":1,"last_watched_at":"2025-03-04T20:00:00.000Z","movie":{"title":"Mystery","ids":{}}}]
            """);
        _trakt.Set("GET /sync/watched/shows", """
            [{"plays":1,"last_watched_at":"2025-05-06T20:00:00.000Z","show":{"title":"Severance","ids":{"tvdb":371980}},
              "seasons":[{"number":1,"episodes":[{"number":1,"plays":1,"last_watched_at":"2025-05-06T20:00:00.000Z"},{"number":2,"plays":1,"last_watched_at":"2025-05-07T20:00:00.000Z"}]}]}]
            """);

        var preview = await (await Client().PostAsync(Api("/trakt/import"), null)).Content.ReadFromJsonAsync<TraktImportPreview>();
        var nothingYet = await InDbAsync(db => db.PlaybackStates.CountAsync());
        var confirm = await Client().PostAsync(Api("/trakt/import?confirm=true"), null);
        await _server.Services.GetRequiredService<TraktImports>().WaitAsync(Owner);

        Assert.Equal(new TraktImportPreview(TraktFilms: 3, TraktEpisodes: 2, InLibrary: 2, NewToFlexarr: 2, NotOnServer: 2, Unmatched: 1, ToTrakt: 1), preview);
        Assert.Equal(1, nothingYet);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        var states = await InDbAsync(db => db.PlaybackStates.ToDictionaryAsync(p => p.MediaItemId));
        Assert.True(states[dune.Id].Watched);
        Assert.True(states[episode.Id].Watched);
        Assert.Equal(new DateTime(2025, 1, 2, 20, 0, 0, DateTimeKind.Utc), states[dune.Id].LastPlayedAt.ToUniversalTime());
        Assert.Equal(2, await InDbAsync(db => db.TraktHistoryHolds.CountAsync()));
        Assert.Contains("\"tmdb\":949", (await OutboxAsync()).Single().Payload, StringComparison.Ordinal);
        Assert.NotNull((await StatusAsync()).GetProperty("importedAt").GetString());
    }

    [Fact]
    public async Task AHeldTitle_ArrivesWatched()
    {
        await SeedAsync(db => db.TraktHistoryHolds.Add(new TraktHistoryHold
        {
            ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = "329865", WatchedAt = new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        }));
        var arrival = Film("Arrival", "329865");
        await SeedAsync(db => db.AddRange(Films, arrival));

        TraktHolds.LookAgain();
        var applied = await InDbAsync(db => TraktHolds.ApplyAsync(db, default));
        var state = await InDbAsync(db => db.PlaybackStates.SingleAsync());

        Assert.Equal(1, applied);
        Assert.True(state.Watched);
        Assert.False(await InDbAsync(db => db.TraktHistoryHolds.AnyAsync()));
    }

    // ---- The regular sync ----

    private Task SyncAsync() => InScopeAsync(s => s.GetRequiredService<TraktSync>().SyncAsync(Owner, default));

    private void Activities(string watched = "2025-06-01T00:00:00.000Z", string paused = "2025-06-01T00:00:00.000Z", string watchlist = "2025-06-01T00:00:00.000Z") =>
        _trakt.Set("GET /sync/last_activities", $$$$"""
            {"all":"{{{{watched}}}}","movies":{"watched_at":"{{{{watched}}}}","paused_at":"{{{{paused}}}}"},"episodes":{"watched_at":"{{{{watched}}}}","paused_at":"{{{{paused}}}}"},
             "watchlist":{"updated_at":"{{{{watchlist}}}}"}}
            """);

    [Fact]
    public async Task TheSync_TakesOnlyNewPlays_AndOnlyWhenTraktSaysSomethingChanged()
    {
        var film = Film("Dune", "438631");
        await SeedAsync(db => db.AddRange(Films, film));
        var cursor = DateTime.UtcNow.AddDays(-1);
        await ConnectAsync(importedAt: cursor);
        Activities();
        _trakt.Set("GET /sync/history", $$$$"""[{"id":1,"watched_at":"{{{{DateTime.UtcNow.AddHours(-1):O}}}}","type":"movie","movie":{"ids":{"tmdb":438631}}}]""");

        await SyncAsync();
        await SyncAsync();

        Assert.True((await InDbAsync(db => db.PlaybackStates.SingleAsync())).Watched);
        // The second sync saw nothing new in last_activities, so didn't ask for history again.
        Assert.Equal(1, _trakt.Count("GET /sync/history"));
        Assert.Contains("start_at=", _trakt.Requests.First(r => r.Key == "GET /sync/history").Query, StringComparison.Ordinal);
        var saved = await InDbAsync(db => db.TraktConnections.SingleAsync());
        Assert.True(saved.HistoryCursor > cursor);
        Assert.NotNull(saved.LastSyncAt);
    }

    [Fact]
    public async Task AResumePointFromAnotherApp_BecomesTheirPlace()
    {
        var film = Film("Dune", "438631");
        await SeedAsync(db =>
        {
            db.AddRange(Films, film);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, DurationSeconds = 6000, PositionSeconds = 100, LastPlayedAt = DateTime.UtcNow.AddDays(-2) });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("GET /sync/playback", $$$$"""[{"id":7,"progress":40,"paused_at":"{{{{DateTime.UtcNow.AddHours(-1):O}}}}","type":"movie","movie":{"ids":{"tmdb":438631}}}]""");

        await SyncAsync();

        Assert.Equal(2400, (await InDbAsync(db => db.PlaybackStates.SingleAsync())).PositionSeconds);
    }

    [Fact]
    public async Task Watchlists_JoinUpFirst_ThenRemovalsPassBothWays()
    {
        var dune = Film("Dune", "438631");
        var heat = Film("Heat", "949");
        var arrival = Film("Arrival", "329865");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune, heat, arrival);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 438631, MediaItemId = dune.Id, Title = "Dune", AddedAt = DateTime.UtcNow.AddDays(-5) });
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 949, MediaItemId = heat.Id, Title = "Heat", AddedAt = DateTime.UtcNow.AddDays(-5) });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("GET /sync/watchlist", $$$$"""
            [{"id":1,"listed_at":"{{{{DateTime.UtcNow.AddDays(-1):O}}}}","type":"movie","movie":{"title":"Arrival","year":2016,"ids":{"tmdb":329865}}},
             {"id":2,"listed_at":"{{{{DateTime.UtcNow.AddDays(-1):O}}}}","type":"movie","movie":{"title":"Heat","ids":{"tmdb":949}}}]
            """);

        // First: both lists end up with all three.
        await SyncAsync();
        var first = await InDbAsync(db => db.WatchlistEntries.Where(w => w.RemovedAt == null).ToListAsync());
        var pushed = JsonDocument.Parse(_trakt.Bodies("POST /sync/watchlist").Single()).RootElement;

        // Then Heat goes from Trakt, and Dune is taken off here.
        Activities(watchlist: "2025-06-02T00:00:00.000Z");
        _trakt.Set("GET /sync/watchlist", $$$$"""
            [{"id":1,"listed_at":"{{{{DateTime.UtcNow.AddDays(-1):O}}}}","type":"movie","movie":{"ids":{"tmdb":329865}}},
             {"id":3,"listed_at":"{{{{DateTime.UtcNow.AddDays(-1):O}}}}","type":"movie","movie":{"ids":{"tmdb":438631}}}]
            """);
        await InDbAsync(db => db.WatchlistEntries.Where(w => w.TmdbId == 438631)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.RemovedAt, DateTime.UtcNow).SetProperty(w => w.RemovedReason, WatchlistRemoval.Removed)));
        await SyncAsync();
        var second = await InDbAsync(db => db.WatchlistEntries.Where(w => w.RemovedAt == null).Select(w => w.TmdbId).ToListAsync());

        Assert.Equal([949, 329865, 438631], first.Select(w => w.TmdbId!.Value).Order());
        Assert.Equal(WatchlistSource.Trakt, first.Single(w => w.TmdbId == 329865).Source);
        Assert.Equal(438631, pushed.GetProperty("movies").EnumerateArray().Single().GetProperty("ids").GetProperty("tmdb").GetInt32());
        Assert.Equal([329865], second.OfType<int>());
        Assert.Contains("438631", _trakt.Bodies("POST /sync/watchlist/remove").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullFreeWatchlist_IsShownPlainly()
    {
        var dune = Film("Dune", "438631");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 438631, MediaItemId = dune.Id, Title = "Dune" });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("POST /sync/watchlist", "{}", (HttpStatusCode)420);

        await SyncAsync();

        Assert.True((await StatusAsync()).GetProperty("watchlistFull").GetBoolean());
        // Still on Flexarr's list.
        Assert.True(await InDbAsync(db => db.WatchlistEntries.AnyAsync(w => w.RemovedAt == null)));
    }

    // ---- Lists, recommendations, the admin's view ----

    [Fact]
    public async Task ChosenTraktLists_BecomePersonalCollections_KeptInStep()
    {
        var dune = Film("Dune", "438631");
        await SeedAsync(db => db.AddRange(Films, dune));
        await ConnectAsync();
        Activities();
        _trakt.Set("GET /users/me/lists", """[{"name":"Sci-fi night","item_count":2,"ids":{"trakt":55,"slug":"sci-fi-night"}}]""");
        _trakt.Set("GET /users/likes/lists", """[{"liked_at":"2025-01-01T00:00:00.000Z","list":{"name":"Best of 2024","item_count":9,"ids":{"trakt":77},"user":{"username":"Sam","ids":{"slug":"sam"}}}}]""");
        _trakt.Set("GET /users/dan/lists/55/items", """
            [{"rank":2,"listed_at":"2025-01-01T00:00:00.000Z","type":"movie","movie":{"title":"Arrival","year":2016,"ids":{"tmdb":329865}}},
             {"rank":1,"listed_at":"2025-01-01T00:00:00.000Z","type":"movie","movie":{"title":"Dune","year":2021,"ids":{"tmdb":438631}}}]
            """);

        var lists = await Client().GetFromJsonAsync<List<TraktListDto>>(Api("/trakt/lists"));
        var chose = await Client().PutAsJsonAsync(Api("/trakt/lists"), new { ids = new[] { "dan/55" } });
        await SyncAsync();
        var collection = await InDbAsync(db => db.Collections.Include(c => c.Items).SingleAsync());
        var deleted = await Client().DeleteAsync(Api($"/collections/{collection.Id}"));

        Assert.Equal(["dan/55", "sam/77"], lists!.Select(l => l.Id));
        Assert.True(lists![1].Liked);
        Assert.Equal(HttpStatusCode.NoContent, chose.StatusCode);
        Assert.Equal("Sci-fi night", collection.Name);
        Assert.Equal(CollectionKind.TraktList, collection.Kind);
        Assert.Equal(Owner, collection.OwnerProfileId);
        Assert.Equal(dune.Id, Assert.Single(collection.Items).MediaItemId);
        Assert.Contains("329865", collection.MissingJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // Deleted here, it isn't brought back.
        Assert.Empty((await InDbAsync(db => db.TraktConnections.SingleAsync())).ListIds);
    }

    [Fact]
    public async Task TraktsPicks_AreARowOnDiscover_TitlesHereFirst()
    {
        var seerr = new FakeArr();
        var server = _server.WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Seerr:BaseUrl", "http://seerr.test");
            b.UseSetting("Flexarr:Seerr:ApiKey", "key");
            b.ConfigureTestServices(s => s.AddHttpClient<Flexarr.Integrations.Arr.SeerrClient>().ConfigurePrimaryHttpMessageHandler(() => seerr));
        });
        var dune = Film("Dune", "438631");
        await SeedAsync(db => db.AddRange(Films, dune));
        await ConnectAsync();
        _trakt.Set("GET /recommendations/movies", """[{"title":"Arrival","ids":{"tmdb":329865}},{"title":"Dune","ids":{"tmdb":438631}}]""");
        _trakt.Set("GET /recommendations/shows", "[]");
        seerr.Responses["GET /api/v1/movie/329865"] = """{"id":329865,"title":"Arrival","releaseDate":"2016-11-11","posterPath":"/a.jpg","voteAverage":7.6}""";
        seerr.Responses["GET /api/v1/movie/438631"] = """{"id":438631,"title":"Dune","releaseDate":"2021-10-22","posterPath":"/d.jpg","voteAverage":7.8}""";
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var rows = await client.GetFromJsonAsync<JsonElement>(Api("/requests/discover"));
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "trakt");

        Assert.Equal("For you, from Trakt", row.GetProperty("title").GetString());
        Assert.Equal(["Dune", "Arrival"], row.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(dune.Id, row.GetProperty("items")[0].GetProperty("libraryItemId").GetGuid());
        await server.DisposeAsync();
        seerr.Dispose();
    }

    [Fact]
    public async Task TheAdmin_SeesWhoIsConnected_NeverTheirTokens()
    {
        await SeedAsync(db => db.Profiles.Add(new Profile { Id = "sam", Name = "Sam" }));
        await ConnectAsync("sam", slug: "sam");

        var text = await Client().GetStringAsync(Api("/trakt/profiles"));
        var profiles = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();

        Assert.Equal("connected", profiles.Single(p => p.GetProperty("profileId").GetString() == "sam").GetProperty("state").GetString());
        Assert.Equal("disconnected", profiles.Single(p => p.GetProperty("profileId").GetString() == Owner).GetProperty("state").GetString());
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Fixes from the audit ----

    [Fact]
    public async Task ATitleTraktRefused_StaysOnFlexarrsList()
    {
        var dune = Film("Dune", "438631");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 438631, MediaItemId = dune.Id, Title = "Dune", AddedAt = DateTime.UtcNow.AddDays(-5) });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("POST /sync/watchlist", "{}", (HttpStatusCode)420);

        await SyncAsync();
        // Something else changes on Trakt's list; Dune still isn't there.
        Activities(watchlist: "2025-06-02T00:00:00.000Z");
        await SyncAsync();

        Assert.True(await InDbAsync(db => db.WatchlistEntries.AnyAsync(w => w.TmdbId == 438631 && w.RemovedAt == null)));
        Assert.Equal(2, _trakt.Count("POST /sync/watchlist"));
    }

    [Fact]
    public async Task TheFirstWatchlistSync_OnlyAdds()
    {
        var dune = Film("Dune", "438631");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune);
            db.WatchlistEntries.Add(new WatchlistEntry
            {
                ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 438631, MediaItemId = dune.Id, Title = "Dune", RemovedAt = DateTime.UtcNow.AddDays(-1),
                RemovedReason = WatchlistRemoval.Removed,
            });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("GET /sync/watchlist", $$$$"""[{"id":1,"listed_at":"{{{{DateTime.UtcNow.AddDays(-30):O}}}}","type":"movie","movie":{"ids":{"tmdb":438631}}}]""");

        await SyncAsync();

        Assert.Equal(0, _trakt.Count("POST /sync/watchlist/remove"));
    }

    [Fact]
    public async Task ARefreshThatFails_KeepsTheTokens_AndDiscoverStillWorks()
    {
        await ConnectAsync(expires: DateTime.UtcNow.AddMinutes(30));
        _trakt.Set("RELAY refresh", """{"reason":"NotConfigured"}""", HttpStatusCode.ServiceUnavailable);

        string? token = null;
        await InScopeAsync(async s => token = await s.GetRequiredService<TraktAccounts>().TokenAsync(Owner, force: false, default));
        var saved = await InDbAsync(db => db.TraktConnections.SingleAsync());

        Assert.Equal("access-token-0123456789", token);
        Assert.Equal("refresh-token-0123456789", saved.RefreshToken);
        Assert.Equal(TraktStatus.Connected, saved.Status);
    }

    [Fact]
    public async Task QueuedPlays_WaitForASignIn_RatherThanGo()
    {
        await ConnectAsync();
        await SeedAsync(db => db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = """{"movies":[{"ids":{"tmdb":1}}]}""" }));
        await InDbAsync(db => db.TraktConnections.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, TraktStatus.NeedsSignIn)));

        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));

        var kept = Assert.Single(await OutboxAsync());
        Assert.True(kept.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(0, _trakt.Count("POST /sync/history"));
    }

    [Fact]
    public async Task ConnectingAnotherAccount_StartsClean()
    {
        await ConnectAsync(slug: "someone-else");
        await SeedAsync(db =>
        {
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.History, Payload = "{}" });
            db.TraktHistoryHolds.Add(new TraktHistoryHold { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = "1", WatchedAt = DateTime.UtcNow });
        });
        _server.Services.GetRequiredService<TraktConnector>().MinimumInterval = TimeSpan.FromMilliseconds(20);
        _trakt.Set("RELAY device", FakeTrakt.Token());

        await Client().PostAsync(Api("/trakt/connect"), null);
        await WaitForAsync(s => s.GetProperty("username").GetString() == "Dan" && s.GetProperty("pending").GetProperty("state").GetString() == "Connected");

        Assert.Equal("dan", (await InDbAsync(db => db.TraktConnections.SingleAsync())).UserSlug);
        Assert.False(await InDbAsync(db => db.TraktOutbox.AnyAsync()));
        Assert.False(await InDbAsync(db => db.TraktHistoryHolds.AnyAsync()));
    }

    [Fact]
    public async Task MakingAProfileAKidsProfile_DisconnectsItsTrakt()
    {
        await SeedAsync(db => db.Profiles.Single(p => p.Id == Owner).SetPin("2468"));
        var kid = (await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Sam" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await ConnectAsync(kid, slug: "sam");
        _trakt.Set("RELAY revoke", "{}");

        var changed = await Client().PutAsJsonAsync(Api($"/profiles/{kid}/age-limit"), new { maxRatingAge = 8, isKids = true });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False(await InDbAsync(db => db.TraktConnections.AnyAsync()));
        Assert.Contains(_trakt.RelayBodies, b => b.Contains("revoke", StringComparison.Ordinal));
    }

    // ---- Fixes from the follow-up audit ----

    [Fact]
    public async Task ATitleAddedBackAfterGoingOffBothLists_Stays()
    {
        var dune = Film("Dune", "438631");
        await SeedAsync(db =>
        {
            db.AddRange(Films, dune);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = Owner, Kind = MediaKind.Movie, TmdbId = 438631, MediaItemId = dune.Id, Title = "Dune", AddedAt = DateTime.UtcNow.AddDays(-5) });
        });
        await ConnectAsync();
        Activities();
        _trakt.Set("GET /sync/watchlist", $$$$"""[{"id":1,"listed_at":"{{{{DateTime.UtcNow.AddDays(-5):O}}}}","type":"movie","movie":{"ids":{"tmdb":438631}}}]""");
        await SyncAsync();

        // Taken off here; the sync takes it off Trakt.
        await Client().DeleteAsync(Api($"/watchlist?libraryItemId={dune.Id}"));
        Activities(watchlist: "2025-06-02T00:00:00.000Z");
        await SyncAsync();
        // Added back here; Trakt doesn't have it.
        _trakt.Set("GET /sync/watchlist", "[]");
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = dune.Id });
        Activities(watchlist: "2025-06-03T00:00:00.000Z");
        await SyncAsync();

        Assert.True(await InDbAsync(db => db.WatchlistEntries.AnyAsync(w => w.TmdbId == 438631 && w.RemovedAt == null)));
        Assert.Contains("438631", _trakt.Bodies("POST /sync/watchlist").Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldEpisode_AppliesWhenItArrives_ForAShowAlreadyHere()
    {
        var (library, show, episode, all) = Show("371980");
        await SeedAsync(db => { db.Libraries.Add(library); db.MediaItems.AddRange(all); });
        TraktHolds.LookAgain();
        await InDbAsync(db => TraktHolds.ApplyAsync(db, default));
        await SeedAsync(db => db.TraktHistoryHolds.Add(new TraktHistoryHold
        {
            ProfileId = Owner, Kind = MediaKind.Episode, TvdbId = "371980", SeasonNumber = 1, EpisodeNumber = 2, WatchedAt = DateTime.UtcNow.AddDays(-1),
        }));
        await InDbAsync(db => TraktHolds.ApplyAsync(db, default));

        // Episode 2 arrives in the show that was already here.
        var season = await InDbAsync(db => db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Season));
        var second = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Half Loop", SortTitle = "2", SeasonNumber = 1, EpisodeNumber = 2, ParentId = season.Id };
        await SeedAsync(db => db.MediaItems.Add(second));
        var applied = await InDbAsync(db => TraktHolds.ApplyAsync(db, default));

        Assert.Equal(1, applied);
        Assert.True((await InDbAsync(db => db.PlaybackStates.SingleAsync(p => p.MediaItemId == second.Id))).Watched);
    }

    [Fact]
    public async Task ANewerScrobble_KeepsAnotherTitlesFinish_AsHistory()
    {
        await ConnectAsync();
        await SeedAsync(db =>
        {
            db.TraktOutbox.Add(new TraktOutboxEntry
            {
                ProfileId = Owner, Kind = TraktOutboxKind.Scrobble, Action = "stop", NextAttemptAt = DateTime.UtcNow.AddMinutes(1),
                Payload = """{"movie":{"ids":{"tmdb":1}},"progress":96}""",
            });
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = Owner, Kind = TraktOutboxKind.Scrobble, Action = "start", Payload = """{"movie":{"ids":{"tmdb":2}},"progress":1}""" });
        });

        await InScopeAsync(s => TraktOutboxWorker.SendDueAsync(s, NullLogger.Instance, default, TimeSpan.Zero));
        var left = Assert.Single(await OutboxAsync());

        Assert.Equal(TraktOutboxKind.History, left.Kind);
        Assert.Contains("\"tmdb\":1", left.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsCantBeSaved_WhileTraktCantBeRead_AndNothingChanges()
    {
        await ConnectAsync();
        await InDbAsync(db => db.TraktConnections.ExecuteUpdateAsync(u => u.SetProperty(c => c.ListIds, new List<string> { "dan/55" })));
        _trakt.Set("GET /users/me/lists", "{}", HttpStatusCode.ServiceUnavailable);

        var saved = await Client().PutAsJsonAsync(Api("/trakt/lists"), new { ids = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadGateway, saved.StatusCode);
        Assert.Equal(["dan/55"], (await InDbAsync(db => db.TraktConnections.SingleAsync())).ListIds);
    }

    [Fact]
    public async Task SettingsFiles_CarryOnlyTheTraktApp_AndOlderFilesLeaveItAlone()
    {
        var path = Flexarr.Api.Configuration.ServerConfigStore.PathFor(_factory.DataDirectory);
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path) : "{}")!.AsObject();
        config["trakt"] = new System.Text.Json.Nodes.JsonObject { ["clientId"] = "mine", ["clientSecret"] = "secret", ["relayUrl"] = "http://elsewhere.test" };
        config["tmdb"] = new System.Text.Json.Nodes.JsonObject { ["apiKey"] = "tmdb-key" };
        await File.WriteAllTextAsync(path, config.ToJsonString());

        System.Text.Json.Nodes.JsonObject exported = null!;
        await InScopeAsync(async s => exported = await Flexarr.Api.Backups.SettingsTransfer.ExportAsync(_factory.DataDirectory, s.GetRequiredService<FlexarrDbContext>(), DateTime.UtcNow, default));
        var older = System.Text.Json.Nodes.JsonNode.Parse("""{"format":"flexarr-settings","version":1,"settings":{"seerr":{"baseUrl":"http://seerr.test","apiKey":"k"}}}""")!.AsObject();
        await InScopeAsync(s => Flexarr.Api.Backups.SettingsTransfer.ApplyAsync(older, new Flexarr.Api.Backups.SettingsImportRequest(["connections"]), _factory.DataDirectory,
            s.GetRequiredService<FlexarrDbContext>(), _factory.ScanQueue, default));
        var after = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();

        Assert.Equal("mine", exported["settings"]!["trakt"]!["clientId"]!.GetValue<string>());
        Assert.Null(exported["settings"]!["trakt"]!["relayUrl"]);
        Assert.Equal("mine", after["trakt"]!["clientId"]!.GetValue<string>());
        Assert.Equal("tmdb-key", after["tmdb"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task KidsProfiles_HaveNoTrakt()
    {
        // A kids profile needs the main profile to have a PIN, and an age limit.
        await SeedAsync(db => db.Profiles.Single(p => p.Id == Owner).SetPin("2468"));
        var kid = (await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Kid", maxRatingAge = 8, isKids = true })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Playroom TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        await client.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { });

        var response = await client.GetAsync(Api("/trakt/status"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

internal static class TraktJsonForTests
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}
