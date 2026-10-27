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
        Assert.Equal(session, started.GetProperty("sessionId").GetString());
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
        Assert.Equal(playing, first.GetProperty("sessionId").GetString());
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
