using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Flexarr.Api.Notifications;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.LiveTv;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Core.Podcasts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

/// <summary>
/// Phones signing up for notifications, and what the worker tells them: each test has a server of its own, since
/// the worker's cursors are the server's.
/// </summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeRelay _relay = new();
    private readonly FakeArr _arr = new();
    private readonly Clock _clock = new(new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _arr.Serve(_factory).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<PushRelay>().ConfigurePrimaryHttpMessageHandler(() => _relay)));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _relay.Dispose();
        _arr.Dispose();
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A phone signed in as a device of its own, as the app is.</summary>
    private async Task<(string Id, HttpClient Client)> PhoneAsync()
    {
        var body = await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = "iPhone" })).Content.ReadFromJsonAsync<JsonElement>();
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("token").GetString()!);
        return (body.GetProperty("id").GetString()!, client);
    }

    private async Task<HttpResponseMessage> RegisterAsync(HttpClient phone, string token = "ab12", bool upcoming = false, int upcomingHour = 9, bool useCurrentProfile = false,
        bool requests = true) =>
        await phone.PutAsJsonAsync(Api("/notifications/device"), new
        {
            requests,
            token = string.Concat(Enumerable.Repeat(token, 16)),
            environment = "sandbox",
            key = Convert.ToBase64String(_key),
            timeZone = "Europe/London",
            upcoming,
            upcomingHour,
            useCurrentProfile,
        });

    private NotificationWorker Worker() => new(_server.Services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<NotificationWorker>.Instance);

    /// <summary>Runs the worker at <paramref name="at"/>, returning what it sent, unsealed.</summary>
    private async Task<List<JsonElement>> RunAtAsync(DateTime at)
    {
        _clock.Now = at;
        var before = _relay.Sent.Count;
        await Worker().RunOnceAsync(CancellationToken.None);
        return _relay.Sent.Skip(before)
            .Select(s => JsonDocument.Parse(PushRelay.Open(_key, s.GetProperty("payload").GetString()!)).RootElement).ToList();
    }

    private async Task SeedAsync(Action<FlexarrDbContext> seed)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>A library made long enough ago that its first scan is over.</summary>
    private static Library OldLibrary(LibraryType type, string name = "Library") =>
        new() { Name = $"{name} {Guid.NewGuid():N}", Type = type, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

    private static MediaItem Film(Library library, string title, DateTime added, string? tmdb = null) => new()
    {
        LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant(), Year = 2020, TmdbId = tmdb, AddedAt = added,
        PosterUrl = "https://image.tmdb.org/t/p/original/poster.jpg",
    };

    /// <summary>The worker's first run only notes where things stand; this gets that out of the way.</summary>
    private async Task StartAsync() => Assert.Empty(await RunAtAsync(_clock.Now));

    [Fact]
    public async Task Registers_ForTheProfileItsOn_AndChecksWhatItsSent()
    {
        var (_, phone) = await PhoneAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync(Api("/notifications/device"))).StatusCode);
        var bad = await phone.PutAsJsonAsync(Api("/notifications/device"), new { token = "nothex", environment = "prod", key = "c2hvcnQ=", upcomingHour = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("token", out _) && errors.TryGetProperty("environment", out _) && errors.TryGetProperty("key", out _));

        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(phone, upcoming: true, upcomingHour: 8)).StatusCode);
        var device = await phone.GetFromJsonAsync<JsonElement>(Api("/notifications/device"));
        Assert.Equal("owner", device.GetProperty("profileId").GetString());
        Assert.True(device.GetProperty("films").GetBoolean());
        Assert.True(device.GetProperty("upcoming").GetBoolean());
        Assert.Equal(8, device.GetProperty("upcomingHour").GetInt32());
        Assert.Equal("Europe/London", device.GetProperty("timeZone").GetString());
        Assert.True(device.GetProperty("relay").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync(Api("/notifications/device"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync(Api("/notifications/device"))).StatusCode);
    }

    [Fact]
    public async Task AnAndroidPhone_RegistersItsFirebaseToken_AndItsPushesSaySo()
    {
        var (_, phone) = await PhoneAsync();
        // Case matters in a Firebase token, and it isn't hex.
        var token = "dGVzdA:APA91bH" + new string('x', 140);
        var android = new { token, environment = "production", key = Convert.ToBase64String(_key), timeZone = "Europe/London", platform = "android" };

        var registered = await phone.PutAsJsonAsync(Api("/notifications/device"), android);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await phone.PostAsync(Api("/notifications/device/test"), null)).StatusCode);
        var sent = _relay.Sent[^1];
        Assert.Equal(token, sent.GetProperty("token").GetString());
        Assert.Equal("android", sent.GetProperty("platform").GetString());

        var refused = await phone.PutAsJsonAsync(Api("/notifications/device"), new { token = "not a token!", environment = "production", key = Convert.ToBase64String(_key), platform = "android" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var unknown = await phone.PutAsJsonAsync(Api("/notifications/device"), new { token, environment = "production", key = Convert.ToBase64String(_key), platform = "windows" });
        Assert.True((await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("platform", out _));

        // An iPhone's pushes don't name a platform, as relays from before Android expect.
        var (_, iPhone) = await PhoneAsync();
        await RegisterAsync(iPhone, "9f8e");
        await iPhone.PostAsync(Api("/notifications/device/test"), null);
        Assert.False(_relay.Sent[^1].TryGetProperty("platform", out _));
    }

    [Fact]
    public async Task WithTheApiKey_APhoneManagesItsRegistration_OnlyWhileItsTheOnlyOneSignedInThatWay()
    {
        // The key gives no sign-in of its own: with two phones on it, either registration could be someone else's.
        var keyPhone = Admin();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(keyPhone, "cd34")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await keyPhone.GetAsync(Api("/notifications/device"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(Admin(), "ef56")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await keyPhone.GetAsync(Api("/notifications/device"))).StatusCode);
        await keyPhone.DeleteAsync(Api("/notifications/device"));
        Assert.Equal(2, await _server.Services.CreateScope().ServiceProvider.GetRequiredService<FlexarrDbContext>().NotificationDevices.CountAsync(d => d.SignInId == null));
    }

    [Fact]
    public async Task OnePhone_HearsAboutOneProfile_TheOneItWasOn()
    {
        var sam = await (await Admin().PostAsJsonAsync(Api("/profiles"), new { name = "Sam" })).Content.ReadFromJsonAsync<JsonElement>();
        var (_, phone) = await PhoneAsync();
        // Choosing a profile on the phone is what its sign-in watches as from then on.
        Assert.True((await phone.PostAsJsonAsync(Api($"/profiles/{sam.GetProperty("id").GetString()}/select"), new { })).IsSuccessStatusCode);
        await RegisterAsync(phone);

        var device = await phone.GetFromJsonAsync<JsonElement>(Api("/notifications/device"));
        Assert.Equal("Sam", device.GetProperty("profileName").GetString());

        // Someone else picks up the phone and chooses their profile: the app registering again (as it does on launch)
        // doesn't hand them Sam's notifications. Only asking for it does.
        Assert.True((await phone.PostAsJsonAsync(Api("/profiles/owner/select"), new { })).IsSuccessStatusCode);
        await RegisterAsync(phone);
        Assert.Equal("Sam", (await phone.GetFromJsonAsync<JsonElement>(Api("/notifications/device"))).GetProperty("profileName").GetString());
        await RegisterAsync(phone, useCurrentProfile: true);
        Assert.Equal("owner", (await phone.GetFromJsonAsync<JsonElement>(Api("/notifications/device"))).GetProperty("profileId").GetString());
        Assert.True((await phone.PostAsJsonAsync(Api($"/profiles/{sam.GetProperty("id").GetString()}/select"), new { })).IsSuccessStatusCode);
        await RegisterAsync(phone, useCurrentProfile: true);

        // Sam has watched it (on another device); the main profile hasn't. Only Sam's phone is listening.
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        var film = Film(library, "Arrival", _clock.Now.AddMinutes(1));
        var other = Film(library, "Heat", _clock.Now.AddMinutes(1));
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.AddRange(film, other);
            db.PlaybackStates.Add(new PlaybackState { UserId = sam.GetProperty("id").GetString()!, MediaItemId = film.Id, Watched = true });
        });

        var sent = await RunAtAsync(_clock.Now.AddMinutes(5));

        Assert.Equal("New film: Heat (2020)", Assert.Single(sent).GetProperty("t").GetString());
    }

    [Fact]
    public async Task NewFilm_IsTold_Once_WithArtworkAndWhatToPlay()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        var film = Film(library, "Arrival", _clock.Now.AddMinutes(1));
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.Add(film);
        });

        // Not yet: it's still settling.
        Assert.Empty(await RunAtAsync(_clock.Now.AddMinutes(2)));
        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(3)));
        Assert.Empty(await RunAtAsync(_clock.Now.AddMinutes(1)));

        Assert.Equal("New film: Arrival (2020)", sent.GetProperty("t").GetString());
        Assert.Equal("flexarr.video", sent.GetProperty("c").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", sent.GetProperty("i").GetString());
        Assert.Equal(film.Id.ToString(), sent.GetProperty("u").GetProperty("play").GetString());
        // The relay was handed only the sealed payload, under the phone's token.
        Assert.Equal(string.Concat(Enumerable.Repeat("ab12", 16)), _relay.Sent[^1].GetProperty("token").GetString());
        Assert.Equal("sandbox", _relay.Sent[^1].GetProperty("environment").GetString());
    }

    [Fact]
    public async Task Replacements_FirstScans_AndHiddenLibraries_AreNotNews()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        var brandNew = new Library { Name = "Just added", Type = LibraryType.Movies, CreatedAt = _clock.Now };
        var hidden = OldLibrary(LibraryType.Movies, "Kids");
        var oldCopy = Film(library, "Heat", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), tmdb: "949");
        oldCopy.MissingSince = _clock.Now; // the file Radarr's replacing, in a renamed folder
        await SeedAsync(db =>
        {
            db.Libraries.AddRange(library, brandNew, hidden);
            db.MediaItems.AddRange(oldCopy, Film(library, "Heat", _clock.Now.AddMinutes(1), tmdb: "949"), Film(brandNew, "Alien", _clock.Now.AddMinutes(1)),
                Film(hidden, "Cars", _clock.Now.AddMinutes(1)));
            db.Profiles.Single(p => p.Id == PlaybackState.DefaultUser).HiddenLibraryIds = [hidden.Id];
        });

        Assert.Empty(await RunAtAsync(_clock.Now.AddMinutes(5)));
    }

    [Fact]
    public async Task EpisodesOfAShow_AreOneNotification()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.TvShows);
        var added = _clock.Now.AddMinutes(1);
        var old = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Slow Horses", SortTitle = "slow horses", AddedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var oldSeason = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 4", SortTitle = "4", SeasonNumber = 4, Parent = old, AddedAt = added };
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Severance", SortTitle = "severance", AddedAt = added };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, Parent = show, AddedAt = added };
        var episodes = Enumerable.Range(1, 3).Select(n => new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Episode, Title = $"Episode {n}", SortTitle = $"{n}", SeasonNumber = 1, EpisodeNumber = n, Parent = season, AddedAt = added,
        }).ToList();
        var next = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Hello Goodbye", SortTitle = "1", SeasonNumber = 4, EpisodeNumber = 1, Parent = oldSeason, AddedAt = added };
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.AddRange([old, oldSeason, show, season, .. episodes, next]);
        });

        var sent = (await RunAtAsync(_clock.Now.AddMinutes(5))).ToDictionary(s => s.GetProperty("t").GetString()!);

        Assert.Equal(2, sent.Count);
        Assert.Equal("Season 1 · 3 episodes", sent["New show: Severance"].GetProperty("b").GetString());
        Assert.Equal(episodes[0].Id.ToString(), sent["New show: Severance"].GetProperty("u").GetProperty("play").GetString());
        Assert.Equal("New episode · S4 E1 · Hello Goodbye", sent["Slow Horses"].GetProperty("b").GetString());
        Assert.Equal($"item:{old.Id}", sent["Slow Horses"].GetProperty("g").GetString());
    }

    [Fact]
    public async Task ARequestedBookComingIn_IsReadyToListen()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Audiobooks);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Audiobook, BookForeignId = "hc:428812", BookArrId = "21",
                Title = "The Impossible Fortune by Richard Osman", RequestedAt = _clock.Now.AddDays(-2) });
            db.MediaItems.Add(new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "The Impossible Fortune", SortTitle = "impossible fortune", AddedAt = _clock.Now.AddMinutes(1),
                Book = new Flexarr.Core.Books.BookDetails { ArrBookId = "21" },
            });
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal(("Your request is ready", "The Impossible Fortune is ready to listen."), (sent.GetProperty("t").GetString(), sent.GetProperty("b").GetString()));
    }

    [Fact]
    public async Task ARequestedBook_IsLookedForInTheAppItWasRequestedFrom()
    {
        // Book 21 in another app is another book: the request was made in "Spoken".
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Audiobooks);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Audiobook, BookForeignId = "hc:428812", BookArrId = "21",
                BookArrInstance = "Spoken", Title = "The Impossible Fortune by Richard Osman", RequestedAt = _clock.Now.AddDays(-2) });
            db.MediaItems.Add(new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "Some Other Book", SortTitle = "some other book", AddedAt = _clock.Now.AddMinutes(1),
                Book = new Flexarr.Core.Books.BookDetails { ArrBookId = "21", ArrInstance = "Ebooks" },
            });
            db.MediaItems.Add(new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "The Impossible Fortune", SortTitle = "impossible fortune", AddedAt = _clock.Now.AddMinutes(2),
                Book = new Flexarr.Core.Books.BookDetails { ArrBookId = "21", ArrInstance = "Spoken" },
            });
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal("The Impossible Fortune is ready to listen.", sent.GetProperty("b").GetString());
    }

    [Fact]
    public async Task ARequestComingIn_TellsWhoAsked_InsteadOfTheUsualNews()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 438631, RequestedAt = _clock.Now.AddDays(-2) });
            db.MediaItems.Add(Film(library, "Dune", _clock.Now.AddMinutes(1), tmdb: "438631"));
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal("Your request is ready", sent.GetProperty("t").GetString());
        Assert.Equal("Dune (2020) is in the library.", sent.GetProperty("b").GetString());
        using var scope = _server.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaRequests.SingleAsync()).FulfilledAt);
    }

    [Fact]
    public async Task AWatchlistTitleComingIn_TellsWhoWantedIt()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 693134, Title = "Dune: Part Two", AddedAt = _clock.Now.AddDays(-5) });
            db.MediaItems.Add(Film(library, "Dune: Part Two", _clock.Now.AddMinutes(1), tmdb: "693134"));
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal("From your watchlist", sent.GetProperty("t").GetString());
        Assert.Equal("Dune: Part Two (2020) is ready to watch.", sent.GetProperty("b").GetString());
        using var scope = _server.Services.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().WatchlistEntries.SingleAsync();
        Assert.NotNull(entry.ArrivedAt);
        Assert.NotNull(entry.MediaItemId);
    }

    [Fact]
    public async Task AWatchlistTitleInALibraryHiddenFromTheProfile_IsntAnnounced()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var films = OldLibrary(LibraryType.Movies);
        var adults = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.AddRange(films, adults);
            db.Profiles.Single(p => p.Id == PlaybackState.DefaultUser).HiddenLibraryIds = [adults.Id];
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 293660, Title = "Deadpool", AddedAt = _clock.Now.AddDays(-5) });
            db.MediaItems.Add(Film(adults, "Deadpool", _clock.Now.AddMinutes(1), tmdb: "293660"));
        });

        var sent = await RunAtAsync(_clock.Now.AddMinutes(5));

        Assert.DoesNotContain(sent, m => m.GetProperty("t").GetString() == "From your watchlist");
        using var scope = _server.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().WatchlistEntries.SingleAsync()).ArrivedAt);
    }

    [Fact]
    public async Task APhoneWithRequestsOff_StillHearsTheUsualNewsForAWatchlistFilm()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone, requests: false);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 1100, Title = "Wanted", AddedAt = _clock.Now.AddDays(-5) });
            db.MediaItems.Add(Film(library, "Wanted", _clock.Now.AddMinutes(1), tmdb: "1100"));
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.StartsWith("New film", sent.GetProperty("t").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWatchlistTitleAlsoRequested_IsToldOnce()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.WatchlistEntries.Add(new WatchlistEntry { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 438631, Title = "Dune", AddedAt = _clock.Now.AddDays(-2) });
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 438631, RequestedAt = _clock.Now.AddDays(-2) });
            db.MediaItems.Add(Film(library, "Dune", _clock.Now.AddMinutes(1), tmdb: "438631"));
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal("Your request is ready", sent.GetProperty("t").GetString());
    }

    [Fact]
    public async Task PodcastEpisodes_OfShowsFollowed_AfterFollowing()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        var library = OldLibrary(LibraryType.Podcasts);
        var podcast = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Podcast, Title = "The Rest Is History", SortTitle = "rest", AddedAt = _clock.Now.AddDays(-30) };
        var feed = new PodcastFeed { MediaItem = podcast, Url = "https://example.com/feed" };
        var episode = new MediaItem { LibraryId = library.Id, Kind = MediaKind.PodcastEpisode, Title = "The Fall of Rome", SortTitle = "fall", Parent = podcast, AddedAt = _clock.Now.AddMinutes(1) };
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.AddRange(podcast, episode);
            db.PodcastFeeds.Add(feed);
            db.PodcastEpisodes.Add(new PodcastEpisode { MediaItem = episode, FeedId = podcast.Id, EpisodeGuid = "1", AudioUrl = "https://example.com/1.mp3", PublishedAt = _clock.Now });
            db.PodcastSubscriptions.Add(new PodcastSubscription { ProfileId = PlaybackState.DefaultUser, FeedId = podcast.Id, SubscribedAt = _clock.Now.AddDays(-30) });
        });

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(2)));

        Assert.Equal("New: The Rest Is History", sent.GetProperty("t").GetString());
        Assert.Equal("The Fall of Rome", sent.GetProperty("b").GetString());
        Assert.Equal("flexarr.podcast", sent.GetProperty("c").GetString());
        Assert.Equal(episode.Id.ToString(), sent.GetProperty("u").GetProperty("episode").GetString());
    }

    [Fact]
    public async Task FinishedRecordings_TellWhoeverSetThemUp()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        await StartAsync();
        await SeedAsync(db => db.Recordings.AddRange(
            new Recording
            {
                ChannelName = "BBC One", Title = "Doctor Who", Subtitle = "The Star Beast", Season = 14, Episode = 1, State = RecordingState.Completed,
                Start = _clock.Now.AddHours(-1), End = _clock.Now, FinishedAt = _clock.Now.AddMinutes(1), CreatedBy = PlaybackState.DefaultUser,
            },
            new Recording { ChannelName = "ITV", Title = "Someone else's", State = RecordingState.Completed, Start = _clock.Now, End = _clock.Now, FinishedAt = _clock.Now.AddMinutes(1), CreatedBy = "nobody-here" },
            new Recording { ChannelName = "ITV", Title = "Failed", State = RecordingState.Failed, Start = _clock.Now, End = _clock.Now, FinishedAt = _clock.Now.AddMinutes(1) }));

        var sent = Assert.Single(await RunAtAsync(_clock.Now.AddMinutes(5)));

        Assert.Equal("Recorded: Doctor Who", sent.GetProperty("t").GetString());
        Assert.Equal("S14 E1 · The Star Beast · BBC One", sent.GetProperty("b").GetString());
    }

    [Fact]
    public async Task Upcoming_EachMorning_IsWhatsOnForThisProfile()
    {
        // The calendar is asked for by the real date, so the phone's morning is made to be now.
        var now = DateTime.UtcNow;
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone, upcoming: true, upcomingHour: TimeZoneInfo.ConvertTimeFromUtc(now, london).Hour);
        var today = now.Date;
        var range = $"start={today.AddDays(-2):yyyy-MM-dd}&end={today.AddDays(2):yyyy-MM-dd}&includeSeries=true&includeEpisodeImages=true&unmonitored=false";
        _arr.Responses[$"GET //sonarr.test/api/v3/calendar?{range}"] = JsonSerializer.Serialize(new object[]
        {
            new { id = 1, seasonNumber = 5, episodeNumber = 14, title = "Ozymandias", airDateUtc = now, series = new { id = 1, title = "Breaking Bad", tvdbId = 81189 } },
            new { id = 2, seasonNumber = 1, episodeNumber = 1, title = "Pilot", airDateUtc = now, series = new { id = 2, title = "Unwatched Show", tvdbId = 5 } },
        });
        _arr.Responses[$"GET //radarr.test/api/v3/calendar?{range}"] = JsonSerializer.Serialize(new object[]
        {
            new { id = 1, title = "Dune", tmdbId = 438631, digitalRelease = today },
            new { id = 2, title = "Not asked for", tmdbId = 1, digitalRelease = today },
        });
        var library = OldLibrary(LibraryType.TvShows);
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Breaking Bad", SortTitle = "breaking bad", TvdbId = "81189" };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 5", SortTitle = "5", Parent = show };
        var watched = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Blood Money", SortTitle = "9", Parent = season };
        var other = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Unwatched Show", SortTitle = "u", TvdbId = "5" };
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.AddRange(show, season, watched, other);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = watched.Id, Watched = true });
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 438631 });
        });

        var sent = Assert.Single(await RunAtAsync(now));
        Assert.Empty(await RunAtAsync(now.AddMinutes(1)));

        Assert.Equal("On today", sent.GetProperty("t").GetString());
        var lines = sent.GetProperty("b").GetString()!.Split('\n');
        Assert.Equal(["Breaking Bad · S5 E14", "Dune · Digital release"], lines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PhonesThatAreGone_AreForgotten()
    {
        var (signIn, revoked) = await PhoneAsync();
        await RegisterAsync(revoked, token: "cd34");
        var (_, deleted) = await PhoneAsync();
        await RegisterAsync(deleted, token: "ef56");
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync(Api($"/sign-ins/{signIn}"))).StatusCode);
        _relay.Answer = HttpStatusCode.Gone;

        await StartAsync();
        var library = OldLibrary(LibraryType.Movies);
        await SeedAsync(db =>
        {
            db.Libraries.Add(library);
            db.MediaItems.Add(Film(library, "Arrival", _clock.Now.AddMinutes(1)));
        });
        await RunAtAsync(_clock.Now.AddMinutes(5));

        using var scope = _server.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().NotificationDevices.ToListAsync());
        // The revoked one wasn't even tried.
        Assert.Single(_relay.Sent);
    }

    [Fact]
    public async Task TestNotification_SaysWhyItFailed()
    {
        var (_, phone) = await PhoneAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsync(Api("/notifications/device/test"), null)).StatusCode);
        await RegisterAsync(phone);

        Assert.Equal(HttpStatusCode.Accepted, (await phone.PostAsync(Api("/notifications/device/test"), null)).StatusCode);
        Assert.Equal("flexarr.test", JsonDocument.Parse(PushRelay.Open(_key, _relay.Sent[^1].GetProperty("payload").GetString()!)).RootElement.GetProperty("c").GetString());

        _relay.Answer = HttpStatusCode.BadGateway;
        var failed = await phone.PostAsync(Api("/notifications/device/test"), null);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("TooSlow", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceAskingToConnect_IsToldToTheMainProfilesPhones()
    {
        var (_, phone) = await PhoneAsync();
        await RegisterAsync(phone);
        var tv = _server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.80");

        var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Living room" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = started.GetProperty("code").GetString()!;
        // Sent in the background, so the TV isn't kept waiting for it.
        for (var tries = 0; _relay.Sent.Count == 0 && tries < 100; tries++)
        {
            await Task.Delay(50);
        }

        var sent = Assert.Single(_relay.Sent);
        var message = JsonDocument.Parse(PushRelay.Open(_key, sent.GetProperty("payload").GetString()!)).RootElement;
        Assert.Equal("flexarr.pairing", message.GetProperty("c").GetString());
        Assert.Equal("Living room wants to connect", message.GetProperty("t").GetString());
        Assert.Contains(code, message.GetProperty("b").GetString(), StringComparison.Ordinal);
        Assert.Equal(code, message.GetProperty("u").GetProperty("pairing").GetString());
        Assert.Equal($"pairing-{code}", sent.GetProperty("collapseId").GetString());
    }

    [Fact]
    public void Sealing_IsCryptoKitsLayout()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var sealedBox = Convert.FromBase64String(PushRelay.Seal(key, "hello"u8.ToArray()));

        Assert.Equal(12 + 5 + 16, sealedBox.Length);
        Assert.Equal("hello"u8.ToArray(), PushRelay.Open(key, Convert.ToBase64String(sealedBox)));
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>Stands in for the relay: keeps what it's sent, and answers as told.</summary>
    [Fact]
    public async Task APhonesLiveActivity_HearsATvsTimes_OnlyWhenTheyChange_AndEndsWithIt()
    {
        var (_, phone) = await PhoneAsync();
        var tv = await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Living room" })).Content.ReadFromJsonAsync<JsonElement>();
        var tvId = tv.GetProperty("id").GetString()!;
        var tvClient = _server.CreateClient();
        tvClient.DefaultRequestHeaders.Add("X-Api-Key", tv.GetProperty("token").GetString()!);
        Guid itemId;
        using (var scope = _server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Heat", SortTitle = "HEAT" };
            db.AddRange(library, film);
            await db.SaveChangesAsync();
            itemId = film.Id;
        }

        // The test server runs no background services: the publisher and the Live Activity worker, by hand, quicker.
        var activities = _server.Services.GetRequiredService<Flexarr.Api.Devices.LiveActivities>();
        activities.Gather = TimeSpan.FromMilliseconds(100);
        activities.EndGrace = TimeSpan.FromMilliseconds(600);
        using var publisher = new Flexarr.Api.Devices.NowPlayingPublisher(_server.Services.GetRequiredService<Flexarr.Api.Devices.NowPlayingFeed>(),
            _server.Services.GetRequiredService<Flexarr.Api.Playback.PlaybackSessions>(), activities,
            _server.Services.GetRequiredService<Flexarr.Api.Devices.DeviceCommands>(), _server.Services.GetRequiredService<Authentication.SignIns>(),
            _server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<Flexarr.Api.Devices.NowPlayingPublisher>.Instance);
        using var worker = new Flexarr.Api.Devices.LiveActivityWorker(activities, _server.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<Flexarr.Api.Devices.LiveActivityWorker>.Instance);
        await publisher.StartAsync(default);
        await worker.StartAsync(default);

        var token = string.Concat(Enumerable.Repeat("cd34", 64)); // 128 bytes, as a Live Activity's are
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsJsonAsync(Api($"/devices/{tvId}/activity"), new { token = "nope", environment = "sandbox", itemId })).StatusCode);
        // A private title reaches a phone with no id: there's nothing to follow.
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsJsonAsync(Api($"/devices/{tvId}/activity"), new { token, environment = "sandbox", itemId = Guid.Empty })).StatusCode);
        // Only a title the phone may see.
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PutAsJsonAsync(Api($"/devices/{tvId}/activity"), new { token, environment = "sandbox", itemId = Guid.NewGuid() })).StatusCode);

        var sessions = _server.Services.GetRequiredService<Flexarr.Api.Playback.PlaybackSessions>();
        var session = new Flexarr.Api.Playback.PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"), ItemId = itemId, ProfileId = PlaybackState.DefaultUser, SignInId = tvId, FileId = Guid.NewGuid(), FilePath = "",
            Container = null, DurationSeconds = 6000, Method = Flexarr.Streaming.PlaybackMethod.DirectPlay, Subtitles = [], CreditsStart = null,
        };
        sessions.Add(session);

        // Registering sends the times straight away, urgently.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync(Api($"/devices/{tvId}/activity"), new { token, environment = "sandbox", itemId })).StatusCode);
        await WaitForAsync(() => _relay.Sent.Count >= 1);
        var first = _relay.Sent[^1];
        Assert.Equal("liveactivity", first.GetProperty("kind").GetString());
        Assert.Equal("update", first.GetProperty("event").GetString());
        // How long ago, not when by this server's clock.
        Assert.InRange(first.GetProperty("state").GetProperty("age").GetDouble(), 0, 5);
        Assert.False(first.GetProperty("state").TryGetProperty("at", out _));

        // Another phone can't take the registration over.
        var (_, other) = await PhoneAsync();
        Assert.NotEqual(HttpStatusCode.NoContent, (await other.PutAsJsonAsync(Api($"/devices/{tvId}/activity"), new { token, environment = "sandbox", itemId })).StatusCode);
        Assert.Equal(token, first.GetProperty("token").GetString());
        Assert.True(first.GetProperty("urgent").GetBoolean());
        // Times only: nothing that says what's playing.
        Assert.DoesNotContain("Heat", first.GetRawText(), StringComparison.Ordinal);

        // Playing on as the clock says: nothing new to send. A seek is, not urgently; a pause is, urgently.
        var sent = _relay.Sent.Count;
        await tvClient.PostAsJsonAsync(Api($"/playback/{session.Id}/progress"), new { positionSeconds = 0.5, paused = false, rate = 1.0, hasNext = false });
        await Task.Delay(500);
        Assert.Equal(sent, _relay.Sent.Count);
        await tvClient.PostAsJsonAsync(Api($"/playback/{session.Id}/progress"), new { positionSeconds = 600.0, paused = false, rate = 1.0, hasNext = false });
        await WaitForAsync(() => _relay.Sent.Count > sent);
        Assert.False(_relay.Sent[^1].GetProperty("urgent").GetBoolean());
        sent = _relay.Sent.Count;
        await tvClient.PostAsJsonAsync(Api($"/playback/{session.Id}/progress"), new { positionSeconds = 630.0, paused = true, rate = 1.0, hasNext = false });
        await WaitForAsync(() => _relay.Sent.Count > sent);
        Assert.True(_relay.Sent[^1].GetProperty("state").GetProperty("paused").GetBoolean());
        Assert.True(_relay.Sent[^1].GetProperty("urgent").GetBoolean());

        // Moving on to the next episode stops one playback before starting the next: no end in the gap.
        sent = _relay.Sent.Count;
        await sessions.RemoveAsync(session.Id);
        await Task.Delay(300);
        sessions.Add(session);
        await Task.Delay(900);
        Assert.DoesNotContain(_relay.Sent.Skip(sent), b => b.GetProperty("event").GetString() == "end");

        // It stops on the TV with the relay down: the end is tried again after the back-off, and isn't followed any more.
        activities.Backoff = TimeSpan.FromMilliseconds(400);
        _relay.Answer = HttpStatusCode.BadGateway;
        await sessions.RemoveAsync(session.Id);
        await Task.Delay(1000);
        _relay.Answer = HttpStatusCode.OK;
        await WaitForAsync(() => _relay.Sent[^1].GetProperty("event").GetString() == "end");
        sent = _relay.Sent.Count;
        sessions.Add(session);
        await Task.Delay(500);
        Assert.Equal(sent, _relay.Sent.Count);
        await worker.StopAsync(default);
        await publisher.StopAsync(default);
    }

    [Fact]
    public void ALiveActivityUpdate_IsOnlyNews_WhenTheClockWouldBeWrong()
    {
        var sent = new Flexarr.Api.Devices.LiveActivityState(false, 100, 1000, 3000, 1);
        Assert.False(Flexarr.Api.Devices.LiveActivities.IsNews(sent, sent with { Position = 110, At = 1010 }));
        Assert.True(Flexarr.Api.Devices.LiveActivities.IsNews(sent, sent with { Position = 300, At = 1010 }));
        Assert.True(Flexarr.Api.Devices.LiveActivities.IsNews(sent, sent with { Paused = true }));
        Assert.True(Flexarr.Api.Devices.LiveActivities.IsNews(sent, sent with { Rate = 1.5 }));
        Assert.True(Flexarr.Api.Devices.LiveActivities.IsNews(null, sent));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class FakeRelay : HttpMessageHandler
    {
        public List<JsonElement> Sent { get; } = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (Answer == HttpStatusCode.OK)
            {
                Sent.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
            }

            var reason = Answer == HttpStatusCode.Gone ? "Unregistered" : "TooSlow";
            if (Answer == HttpStatusCode.Gone)
            {
                Sent.Add(body);
            }

            return new HttpResponseMessage(Answer) { Content = JsonContent.Create(new { reason }) };
        }
    }
}
