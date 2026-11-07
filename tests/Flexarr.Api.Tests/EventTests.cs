using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Authentication;
using Flexarr.Api.Events;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

/// <summary>The playback event stream Home Assistant follows: started, paused, resumed, credits, ended, stopped.</summary>
public sealed class EventTests : IDisposable
{
    // Its own server, with the publisher running (the test server runs no background services).
    private readonly FlexarrApiFactory factory = new();
    private readonly PlaybackEventPublisher publisher;

    public EventTests()
    {
        publisher = new PlaybackEventPublisher(factory.Services.GetRequiredService<PlaybackEvents>(), factory.Services.GetRequiredService<SignIns>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PlaybackEventPublisher>.Instance);
        publisher.StartAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        publisher.StopAsync(default).GetAwaiter().GetResult();
        publisher.Dispose();
        factory.Dispose();
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static async Task<StreamReader> ListenAsync(HttpClient client)
    {
        var response = await client.GetAsync(Api("/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());
        return reader;
    }

    private static async Task<JsonElement> NextEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended.");
            if (line == "event: playback")
            {
                var data = await reader.ReadLineAsync(timeout.Token);
                Assert.StartsWith("data: ", data, StringComparison.Ordinal);
                return JsonDocument.Parse(data!["data: ".Length..]).RootElement;
            }
        }
    }

    /// <summary>A film with its credits at 9 minutes, playing on a signed-in device (no stream behind it: only progress matters here).</summary>
    private async Task<(string Session, string DeviceId)> PlayingAsync(string deviceName)
    {
        var device = await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName })).Content.ReadFromJsonAsync<JsonElement>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Heat", SortTitle = "heat" };
        db.AddRange(library, movie);
        await db.SaveChangesAsync();

        var session = new PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ItemId = movie.Id,
            ProfileId = PlaybackState.DefaultUser,
            SignInId = device.GetProperty("id").GetString(),
            FileId = Guid.NewGuid(),
            FilePath = "",
            Container = null,
            DurationSeconds = 600,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = 540,
        };
        factory.Services.GetRequiredService<PlaybackSessions>().Add(session);
        return (session.Id, session.SignInId!);
    }

    private Task<HttpResponseMessage> ReportAsync(string session, double position, bool paused = false, bool ended = false) =>
        Admin().PostAsJsonAsync(Api($"/playback/{session}/progress"), new { positionSeconds = position, paused, ended });

    [Fact]
    public async Task AViewing_IsFollowedFromStartToStop()
    {
        var events = await ListenAsync(Admin());
        var (session, deviceId) = await PlayingAsync("Living room");

        await ReportAsync(session, 5);
        var started = await NextEventAsync(events);
        Assert.Equal("Started", started.GetProperty("kind").GetString());
        // The public playback id, never the session id: that works as the password for the stream.
        Assert.Equal(Flexarr.Api.Logging.Redactor.ShortHash(session), started.GetProperty("sessionId").GetString());
        Assert.DoesNotContain(session, started.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(deviceId, started.GetProperty("deviceId").GetString());
        Assert.Equal("Living room", started.GetProperty("deviceName").GetString());
        Assert.Equal("Heat", started.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal("Movie", started.GetProperty("item").GetProperty("kind").GetString());

        // Reports that change nothing say nothing.
        await ReportAsync(session, 15);
        await ReportAsync(session, 20, paused: true);
        Assert.Equal("Paused", (await NextEventAsync(events)).GetProperty("kind").GetString());
        await ReportAsync(session, 20);
        Assert.Equal("Resumed", (await NextEventAsync(events)).GetProperty("kind").GetString());

        await ReportAsync(session, 541);
        var credits = await NextEventAsync(events);
        Assert.Equal("Credits", credits.GetProperty("kind").GetString());
        Assert.Equal(541, credits.GetProperty("positionSeconds").GetDouble());

        // Back before the credits and through them again: lights down, lights up again.
        await ReportAsync(session, 300);
        await ReportAsync(session, 545);
        Assert.Equal("Credits", (await NextEventAsync(events)).GetProperty("kind").GetString());

        await ReportAsync(session, 600, paused: true, ended: true);
        Assert.Equal("Ended", (await NextEventAsync(events)).GetProperty("kind").GetString());

        await Admin().DeleteAsync(Api($"/playback/{session}"));
        Assert.Equal("Stopped", (await NextEventAsync(events)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AQueuedPlayback_DoesNotStart_UntilItPlays()
    {
        var events = await ListenAsync(Admin());
        var (queued, _) = await PlayingAsync("Bedroom");
        var (playing, _) = await PlayingAsync("Kitchen");

        // The queued one is closed without ever playing: nothing to say about it.
        await Admin().DeleteAsync(Api($"/playback/{queued}"));
        await ReportAsync(playing, 1);
        var first = await NextEventAsync(events);
        Assert.Equal("Started", first.GetProperty("kind").GetString());
        Assert.Equal(Flexarr.Api.Logging.Redactor.ShortHash(playing), first.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task ABookInALibraryHiddenFromAProfile_IsToldAsPlaying_WithoutItsChapter()
    {
        // A book with chapters, playing on the study's speaker as the main profile.
        var speaker = await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Study" })).Content.ReadFromJsonAsync<JsonElement>();
        Guid books, book;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
            var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "The Secret History", SortTitle = "secret history" };
            db.AddRange(library, item);
            db.MediaChapters.Add(new Flexarr.Core.Books.MediaChapter { MediaItemId = item.Id, Index = 0, Title = "Prologue: The Snow", StartSeconds = 0, EndSeconds = 900 });
            await db.SaveChangesAsync();
            (books, book) = (library.Id, item.Id);
        }

        var session = new PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"), ItemId = book, ProfileId = PlaybackState.DefaultUser, SignInId = speaker.GetProperty("id").GetString(), FileId = Guid.NewGuid(),
            FilePath = "", Container = null, DurationSeconds = 3600, Method = PlaybackMethod.DirectPlay, Subtitles = [], CreditsStart = null, BookParts = [],
        };

        // A phone on a profile kept out of the audiobooks library, following what plays and (as a remote) what each device plays.
        var sam = (await (await Admin().PostAsJsonAsync(Api("/profiles"), new { name = "Sam", hiddenLibraryIds = new[] { books } })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Sam's phone" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var phone = factory.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.True((await phone.PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { })).IsSuccessStatusCode);
        var forSam = await ListenAsync(phone);
        var forMain = await ListenAsync(Admin());
        var remote = await phone.GetAsync(Api("/devices/commands?nowPlaying=true"), HttpCompletionOption.ResponseHeadersRead);
        var onRemote = new StreamReader(await remote.Content.ReadAsStreamAsync());
        using var nowPlaying = new Flexarr.Api.Devices.NowPlayingPublisher(factory.Services.GetRequiredService<Flexarr.Api.Devices.NowPlayingFeed>(),
            factory.Services.GetRequiredService<PlaybackSessions>(), factory.Services.GetRequiredService<Flexarr.Api.Devices.LiveActivities>(),
            factory.Services.GetRequiredService<Flexarr.Api.Devices.DeviceCommands>(), factory.Services.GetRequiredService<SignIns>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<Flexarr.Api.Devices.NowPlayingPublisher>.Instance);
        await nowPlaying.StartAsync(default);

        factory.Services.GetRequiredService<PlaybackSessions>().Add(session);
        await ReportAsync(session.Id, 30);
        var mine = await NextEventAsync(forMain);
        var theirs = await NextEventAsync(forSam);
        JsonElement onPhone;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (true)
            {
                var line = await onRemote.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended.");
                if (line == "event: now-playing" && JsonDocument.Parse((await onRemote.ReadLineAsync(timeout.Token))!["data: ".Length..]).RootElement is var change
                    && change.GetProperty("nowPlaying").ValueKind == JsonValueKind.Object)
                {
                    onPhone = change.GetProperty("nowPlaying");
                    break;
                }
            }
        }

        await nowPlaying.StopAsync(default);
        remote.Dispose();

        // The main profile hears the book and its chapter.
        Assert.Equal(("The Secret History", "Prologue: The Snow"), (mine.GetProperty("item").GetProperty("title").GetString(), mine.GetProperty("chapter").GetString()));
        // Sam is told something is playing, and nothing that says what: not the title, not its length, not the chapter.
        Assert.Equal(Flexarr.Api.Profiles.LibraryVisibility.PrivateTitle, theirs.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("chapter").ValueKind);
        Assert.DoesNotContain("Snow", theirs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(Flexarr.Api.Profiles.LibraryVisibility.PrivateTitle, onPhone.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, onPhone.GetProperty("chapter").ValueKind);
        Assert.DoesNotContain("Snow", onPhone.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStream_NeedsSigningIn()
    {
        var response = await factory.CreateClient().GetAsync(Api("/events"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void APlaybackThatGoesQuiet_Stops_AndStartsAgainIfItReports()
    {
        var events = new PlaybackEvents();
        var session = new PlaybackSession
        {
            Id = "s",
            ItemId = Guid.NewGuid(),
            ProfileId = PlaybackState.DefaultUser,
            FileId = Guid.NewGuid(),
            FilePath = "",
            Container = null,
            DurationSeconds = 600,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = null,
        };
        var now = DateTime.UtcNow;
        events.Observe(session, new ProgressRequest(10));
        session.LastSeen = now;

        events.Sweep([session], now + TimeSpan.FromMinutes(1));
        events.Sweep([session], now + PlaybackEvents.PlayingSilence + TimeSpan.FromSeconds(1));
        events.Sweep([session], now + PlaybackEvents.PlayingSilence + TimeSpan.FromSeconds(4));
        events.Observe(session, new ProgressRequest(20));

        // A paused one is given longer.
        session.Paused = true;
        events.Sweep([session], now + PlaybackEvents.PlayingSilence + TimeSpan.FromSeconds(1));

        var kinds = new List<PlaybackEventKind>();
        while (events.Noticed.TryRead(out var noticed))
        {
            kinds.Add(noticed.Kind);
        }

        Assert.Equal([PlaybackEventKind.Started, PlaybackEventKind.Stopped, PlaybackEventKind.Started], kinds);
    }
}
