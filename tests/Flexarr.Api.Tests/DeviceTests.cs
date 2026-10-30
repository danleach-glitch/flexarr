using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Devices;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

/// <summary>Playing on another device: the list of devices, commands to them, and the stream a device listens on.</summary>
public class DeviceTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<(string Id, HttpClient Client)> DeviceAsync(string name)
    {
        var response = await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = name });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("token").GetString()!);
        return (body.GetProperty("id").GetString()!, client);
    }

    /// <summary>Opens the device's command stream and reads events off it as they come.</summary>
    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> ListenAsync(HttpClient device, string query = "")
    {
        var response = await device.GetAsync(Api("/devices/commands" + query), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        // The first thing sent is a comment saying the stream is open.
        Assert.Equal(": connected", await reader.ReadLineAsync());
        return (response, reader);
    }

    /// <summary>The next "event: command" on the stream, as JSON.</summary>
    private static Task<JsonElement> NextCommandAsync(StreamReader reader) => NextEventAsync(reader, "command");

    /// <summary>The next event called <paramref name="name"/> on the stream, as JSON.</summary>
    private static async Task<JsonElement> NextEventAsync(StreamReader reader, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended.");
            if (line == $"event: {name}")
            {
                var data = await reader.ReadLineAsync(timeout.Token);
                Assert.StartsWith("data: ", data, StringComparison.Ordinal);
                return JsonDocument.Parse(data!["data: ".Length..]).RootElement;
            }
        }
    }

    [Fact]
    public async Task ADevice_IsListedOffline_UntilItListens()
    {
        var (id, tv) = await DeviceAsync("Living room");

        var before = await Admin().GetFromJsonAsync<JsonElement>(Api("/devices"));
        var listed = before.EnumerateArray().Single(d => d.GetProperty("id").GetString() == id);
        Assert.Equal("Living room", listed.GetProperty("name").GetString());
        Assert.False(listed.GetProperty("online").GetBoolean());

        var (response, _) = await ListenAsync(tv);
        var during = await Admin().GetFromJsonAsync<JsonElement>(Api("/devices"));
        Assert.True(during.EnumerateArray().Single(d => d.GetProperty("id").GetString() == id).GetProperty("online").GetBoolean());

        // The device asking sees itself marked.
        var mine = await tv.GetFromJsonAsync<JsonElement>(Api("/devices"));
        Assert.True(mine.EnumerateArray().Single(d => d.GetProperty("id").GetString() == id).GetProperty("isThisOne").GetBoolean());
        response.Dispose();
    }

    [Fact]
    public async Task PlayingAShowOnADevice_SendsTheEpisodeItsProfileIsUpTo()
    {
        var show = await SeedAsync("Traitors", ("S01E01", 1, 1), ("S01E02", 1, 2), ("S02E01", 2, 1));
        await WatchAsync(show.Episodes["S01E01"]);
        var (id, tv) = await DeviceAsync("Bedroom");
        var (response, reader) = await ListenAsync(tv);

        var sent = await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = show.Series });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var result = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("delivered").GetBoolean());
        Assert.Equal("Bedroom", result.GetProperty("deviceName").GetString());
        Assert.Equal("S01E02", result.GetProperty("item").GetProperty("title").GetString());

        var command = await NextCommandAsync(reader);
        Assert.Equal("Play", command.GetProperty("kind").GetString());
        Assert.Equal(show.Episodes["S01E02"], command.GetProperty("itemId").GetGuid());
        Assert.False(command.GetProperty("fromBeginning").GetBoolean());

        var paused = await Admin().PostAsJsonAsync(Api($"/devices/{id}/pause"), new { });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Pause", (await NextCommandAsync(reader)).GetProperty("kind").GetString());
        response.Dispose();
    }

    [Fact]
    public async Task ChangingADevicesSound_ReachesItOnlyWhileFlexarrIsOpen_AndLeavesAWaitingPlayAlone()
    {
        var show = await SeedAsync("Slow Horses", ("S01E01", 1, 1));
        var (id, tv) = await DeviceAsync("Lounge");

        // Not listening: a play waits for it, and a sound change neither waits nor pushes the play out.
        Assert.Equal(HttpStatusCode.Accepted, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = show.Series })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/sound"), new { nightMode = "On" })).StatusCode);
        var (response, reader) = await ListenAsync(tv);
        Assert.Equal("Play", (await NextCommandAsync(reader)).GetProperty("kind").GetString());

        var sent = await Admin().PostAsJsonAsync(Api($"/devices/{id}/sound"), new { nightMode = "On", dialogueBoost = "High" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var command = await NextCommandAsync(reader);
        Assert.Equal("Sound", command.GetProperty("kind").GetString());
        Assert.Equal("On", command.GetProperty("sound").GetProperty("nightMode").GetString());
        Assert.Equal("High", command.GetProperty("sound").GetProperty("dialogueBoost").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/sound"), new { })).StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task PlayingAsAProfile_SendsTheEpisodeThatProfileIsUpTo_AndTellsTheDeviceWhose()
    {
        var show = await SeedAsync("Taskmaster", ("S01E01", 1, 1), ("S01E02", 1, 2));
        await WatchAsync(show.Episodes["S01E01"]); // the main profile's; Sam hasn't started
        var sam = (await (await Admin().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var (id, tv) = await DeviceAsync("Den");
        var (response, reader) = await ListenAsync(tv);

        var sent = await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = show.Series, profileId = sam });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("S01E01", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("title").GetString());
        var command = await NextCommandAsync(reader);
        Assert.Equal(show.Episodes["S01E01"], command.GetProperty("itemId").GetGuid());
        Assert.Equal(sam, command.GetProperty("profileId").GetString());

        var nobody = await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = show.Series, profileId = "nobody" });
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task PuttingAChannelOnADevice_SendsIt()
    {
        var channel = new Flexarr.Core.LiveTv.LiveChannel { Name = "BBC One", Number = 101, SourceId = 1 };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.LiveChannels.Add(channel);
            await db.SaveChangesAsync();
        }

        var (id, tv) = await DeviceAsync("Lounge");
        var (response, reader) = await ListenAsync(tv);

        var sent = await Admin().PostAsJsonAsync(Api($"/devices/{id}/watch"), new { channelId = channel.Id });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("BBC One", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("channel").GetString());
        var command = await NextCommandAsync(reader);
        Assert.Equal("Watch", command.GetProperty("kind").GetString());
        Assert.Equal(channel.Id, command.GetProperty("channelId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/watch"), new { channelId = Guid.NewGuid() })).StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task ACommandToADeviceThatIsntListening_WaitsForIt()
    {
        var show = await SeedAsync("Curb", ("S01E01", 1, 1));
        var (id, tv) = await DeviceAsync("Kitchen");

        var sent = await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = show.Episodes["S01E01"], fromBeginning = true });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.False((await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivered").GetBoolean());

        // Flexarr is opened on it: the waiting command arrives at once.
        var (response, reader) = await ListenAsync(tv);
        var command = await NextCommandAsync(reader);
        Assert.Equal(show.Episodes["S01E01"], command.GetProperty("itemId").GetGuid());
        Assert.True(command.GetProperty("fromBeginning").GetBoolean());
        response.Dispose();
    }

    [Fact]
    public async Task OnlyADevice_CanListen_AndUnknownDevicesArentFound()
    {
        var listen = await Admin().GetAsync(Api("/devices/commands"));
        Assert.Equal(HttpStatusCode.Forbidden, listen.StatusCode);

        var play = await Admin().PostAsJsonAsync(Api("/devices/nosuchdevice/play"), new { itemId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, play.StatusCode);

        var (id, _) = await DeviceAsync("Study");
        var missing = await Admin().PostAsJsonAsync(Api($"/devices/{id}/play"), new { itemId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ARemotesCommands_ReachADeviceThatTakesThem_AndNotOneTooOldTo()
    {
        var (id, tv) = await DeviceAsync("Snug");
        var (response, reader) = await ListenAsync(tv, "?accepts=Seek,Skip,Next,SkipSegment,AudioTrack,SubtitleTrack,Rate,Teleport");

        var listed = (await Admin().GetFromJsonAsync<JsonElement>(Api("/devices"))).EnumerateArray().Single(d => d.GetProperty("id").GetString() == id);
        var accepts = listed.GetProperty("accepts").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Contains("Pause", accepts);
        Assert.Contains("Seek", accepts);
        Assert.DoesNotContain("Previous", accepts);

        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/seek"), new { positionSeconds = 754.5 })).StatusCode);
        var seek = await NextCommandAsync(reader);
        Assert.Equal("Seek", seek.GetProperty("kind").GetString());
        Assert.Equal(754.5, seek.GetProperty("seconds").GetDouble());

        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/skip"), new { seconds = -10 })).StatusCode);
        Assert.Equal(-10, (await NextCommandAsync(reader)).GetProperty("seconds").GetDouble());

        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/subtitles"), new { trackId = (string?)null })).StatusCode);
        var subtitles = await NextCommandAsync(reader);
        Assert.Equal("SubtitleTrack", subtitles.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, subtitles.GetProperty("trackId").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/next"), new { })).StatusCode);
        Assert.Equal("Next", (await NextCommandAsync(reader)).GetProperty("kind").GetString());

        // Not said it takes, nonsense, and not open: refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/previous"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/rate"), new { rate = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/seek"), new { positionSeconds = -1 })).StatusCode);
        response.Dispose();
        await WaitUntilAsync(async () => !(await Admin().GetFromJsonAsync<JsonElement>(Api("/devices"))).EnumerateArray()
            .Single(d => d.GetProperty("id").GetString() == id).GetProperty("online").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().PostAsJsonAsync(Api($"/devices/{id}/seek"), new { positionSeconds = 1 })).StatusCode);

        // An app from before remotes says nothing: only the basic commands.
        var (old, oldTv) = await DeviceAsync("Old TV");
        var (oldResponse, _) = await ListenAsync(oldTv);
        var oldListed = (await Admin().GetFromJsonAsync<JsonElement>(Api("/devices"))).EnumerateArray().Single(d => d.GetProperty("id").GetString() == old);
        Assert.False(oldListed.TryGetProperty("accepts", out var none) && none.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().PostAsJsonAsync(Api($"/devices/{old}/seek"), new { positionSeconds = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync(Api($"/devices/{old}/pause"), new { })).StatusCode);
        oldResponse.Dispose();
    }

    [Fact]
    public async Task APhoneAskingForNowPlaying_HearsWhatATvIsPlaying_AsItChanges()
    {
        var show = await SeedAsync("Bluey", ("S01E01", 1, 1));
        var (tvId, tv) = await DeviceAsync("Family room");
        var (_, phone) = await DeviceAsync("Phone");
        var (response, reader) = await ListenAsync(phone, "?nowPlaying=true");
        // The test server runs no background services: this one, by hand.
        using var publisher = new NowPlayingPublisher(factory.Services.GetRequiredService<NowPlayingFeed>(), factory.Services.GetRequiredService<PlaybackSessions>(),
            factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NowPlayingPublisher>.Instance);
        await publisher.StartAsync(default);

        var session = new PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ItemId = show.Episodes["S01E01"],
            ProfileId = PlaybackState.DefaultUser,
            SignInId = tvId,
            FileId = Guid.NewGuid(),
            FilePath = "",
            Container = null,
            DurationSeconds = 420,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = null,
        };
        factory.Services.GetRequiredService<PlaybackSessions>().Add(session);
        var started = await NextEventAsync(reader, "now-playing");
        Assert.Equal(tvId, started.GetProperty("deviceId").GetString());
        Assert.Equal("S01E01", started.GetProperty("nowPlaying").GetProperty("item").GetProperty("title").GetString());

        var report = new
        {
            positionSeconds = 12.0, paused = false, rate = 1.5, prompt = "SkipIntro", hasNext = true,
            tracks = new { audio = new[] { new { id = "1", label = "English", selected = true } }, subtitles = Array.Empty<object>() },
            sound = new { nightMode = "On" },
        };
        Assert.Equal(HttpStatusCode.NoContent, (await tv.PostAsJsonAsync(Api($"/playback/{session.Id}/progress"), report)).StatusCode);
        var playing = (await NextEventAsync(reader, "now-playing")).GetProperty("nowPlaying");
        Assert.Equal(12, playing.GetProperty("positionSeconds").GetDouble());
        Assert.Equal(1.5, playing.GetProperty("rate").GetDouble());
        Assert.Equal("SkipIntro", playing.GetProperty("prompt").GetString());
        Assert.True(playing.GetProperty("hasNext").GetBoolean());
        Assert.Equal("English", playing.GetProperty("tracks").GetProperty("audio")[0].GetProperty("label").GetString());
        Assert.Equal("On", playing.GetProperty("sound").GetProperty("nightMode").GetString());

        // A later report leaving out the tracks keeps them; the prompt goes when it isn't said.
        Assert.Equal(HttpStatusCode.NoContent, (await tv.PostAsJsonAsync(Api($"/playback/{session.Id}/progress"), new { positionSeconds = 40.0, paused = true, rate = 1.0, hasNext = true })).StatusCode);
        var paused = (await NextEventAsync(reader, "now-playing")).GetProperty("nowPlaying");
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("prompt").ValueKind);
        Assert.Equal("English", paused.GetProperty("tracks").GetProperty("audio")[0].GetProperty("label").GetString());

        await factory.Services.GetRequiredService<PlaybackSessions>().RemoveAsync(session.Id);
        var stopped = await NextEventAsync(reader, "now-playing");
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("nowPlaying").ValueKind);
        response.Dispose();
        await publisher.StopAsync(default);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50 && !await condition(); i++)
        {
            await Task.Delay(100);
        }
    }

    private async Task WatchAsync(Guid episode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episode, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid Series, Dictionary<string, Guid> Episodes)> SeedAsync(string title, params (string Title, int Season, int Number)[] episodes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = title, SortTitle = title.ToUpperInvariant() };
        db.AddRange(library, series);
        var seasons = new Dictionary<int, MediaItem>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, number, episode) in episodes)
        {
            if (!seasons.TryGetValue(number, out var season))
            {
                season = seasons[number] = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = $"Season {number}", SortTitle = $"{number:D4}", SeasonNumber = number, Parent = series };
                db.Add(season);
            }

            var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = name, Parent = season, SeasonNumber = number, EpisodeNumber = episode, SortTitle = $"{episode:D4}" };
            db.Add(item);
            ids[name] = item.Id;
        }

        await db.SaveChangesAsync();
        return (series.Id, ids);
    }
}
