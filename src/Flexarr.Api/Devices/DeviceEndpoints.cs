using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Devices;

/// <param name="Paused">Paused on the device (as it last reported).</param>
public sealed record NowPlayingDto(ItemSummaryDto Item, double PositionSeconds, double DurationSeconds, bool Paused);

/// <summary>An Apple TV, iPhone or iPad signed in to this server, and whether it would take a command right now.</summary>
/// <param name="Online">Flexarr is open on it and listening. Offline, a command waits a couple of minutes for it.</param>
/// <param name="ProfileId">Whose history it plays against: the profile chosen on it.</param>
/// <param name="IsThisOne">The device asking.</param>
public sealed record DeviceDto(string Id, string Name, bool Online, DateTime LastUsedAt, string ProfileId, string? ProfileName, bool IsThisOne, NowPlayingDto? NowPlaying);

/// <param name="ItemId">A film, episode or track to play, or a show, season or album: the next episode (or the first track) is worked out for the device's profile.</param>
/// <param name="FromBeginning">Start over rather than resume.</param>
/// <param name="Shuffle">For a show: its episodes in a random order, one after another.</param>
public sealed record DevicePlayRequest(Guid ItemId, bool FromBeginning = false, bool Shuffle = false);

/// <param name="Delivered">The device took the command. False: Flexarr isn't open on it; the command waits a couple of minutes for it to be.</param>
/// <param name="Item">For play: what will play (the episode, when a show was asked for).</param>
/// <param name="Channel">For watch: the channel being put on.</param>
public sealed record DeviceCommandResultDto(bool Delivered, string DeviceName, ItemSummaryDto? Item, string? Channel = null);

public sealed record DeviceWatchRequest(Guid ChannelId);

/// <summary>
/// Devices as targets: list them, tell one to play something (or pause, resume, stop), and the stream a device keeps
/// open to hear such commands. How the phone (and Siri, and an assistant over MCP) plays on the TV.
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>A playback that hasn't reported for this long isn't shown as what the device is playing.</summary>
    private static readonly TimeSpan NowPlayingStaleness = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static RouteGroupBuilder MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/devices").WithTags("Devices");

        group.MapGet("/", async (SignIns signIns, DeviceCommands commands, PlaybackSessions sessions, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
            Results.Ok(await ListAsync(signIns, commands, sessions, db, current, ct)));

        group.MapPost("/{id}/play", async (string id, DevicePlayRequest request, SignIns signIns, DeviceCommands commands, FlexarrDbContext db, PlaybackService playback,
            CurrentProfile current, CancellationToken ct) =>
        {
            if (!current.IsTrusted)
            {
                return Forbidden();
            }

            return await PlayAsync(id, request, signIns, commands, db, playback, current, ct) switch
            {
                { Result: { } result, Delivered: true } => Results.Ok(result),
                { Result: { } result } => Results.Accepted(null as string, result),
                { Error: { } error, Status: var status } => Results.Problem(error, statusCode: status),
                _ => Results.NotFound(),
            };
        });

        // "Put BBC One on the TV": a Live TV channel on another device.
        group.MapPost("/{id}/watch", async (string id, DeviceWatchRequest request, SignIns signIns, DeviceCommands commands, FlexarrDbContext db,
            CurrentProfile current, CancellationToken ct) =>
        {
            if (!current.IsTrusted)
            {
                return Forbidden();
            }

            return await WatchAsync(id, request.ChannelId, signIns, commands, db, current, ct) switch
            {
                { Result: { } result, Delivered: true } => Results.Ok(result),
                { Result: { } result } => Results.Accepted(null as string, result),
                { Error: { } error, Status: var status } => Results.Problem(error, statusCode: status),
                _ => Results.NotFound(),
            };
        });

        foreach (var (path, kind) in new[] { ("pause", DeviceCommandKind.Pause), ("resume", DeviceCommandKind.Resume), ("stop", DeviceCommandKind.Stop) })
        {
            group.MapPost($"/{{id}}/{path}", (string id, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            {
                if (!current.IsTrusted)
                {
                    return Forbidden();
                }

                return Control(id, kind, signIns, commands, current) is { } result
                    ? result.Delivered ? Results.Ok(result) : Results.Accepted(null as string, result)
                    : Results.NotFound();
            });
        }

        // What a device keeps open while Flexarr is in front: commands arrive as server-sent events.
        group.MapGet("/commands", StreamAsync);

        return api;
    }

    private static IResult Forbidden() => Results.Problem("Only a signed-in browser or device, or the API key, can play on other devices.", statusCode: 403);

    /// <summary>Every device signed in (Apple TVs, iPhones, iPads), the ones that would take a command now first.</summary>
    public static async Task<List<DeviceDto>> ListAsync(SignIns signIns, DeviceCommands commands, PlaybackSessions sessions, FlexarrDbContext db, CurrentProfile current,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(current);
        var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var list = new List<DeviceDto>();
        foreach (var device in signIns.List().Where(s => s.Kind == SignInKind.Device))
        {
            var profileId = device.ProfileId is { } chosen && names.ContainsKey(chosen) ? chosen : PlaybackState.DefaultUser;
            NowPlayingDto? nowPlaying = null;
            var playing = sessions.Active.Where(s => s.SignInId == device.Id && DateTime.UtcNow - s.LastSeen < NowPlayingStaleness)
                .OrderByDescending(s => s.LastSeen).FirstOrDefault();
            // A title from a library hidden from this profile shows as playing, but not what it is.
            if (playing is not null && await LibraryVisibility.PlayingAsync(db, playing.ItemId, playing.ProfileId, ct) is { } item)
            {
                nowPlaying = new NowPlayingDto(item, playing.LastPosition, playing.DurationSeconds, playing.Paused);
            }

            list.Add(new DeviceDto(device.Id, device.Name, commands.IsOnline(device.Id), device.LastUsedAt, profileId, names.GetValueOrDefault(profileId),
                device.Id == current.SignInId, nowPlaying));
        }

        return list.OrderByDescending(d => d.Online).ThenByDescending(d => d.LastUsedAt).ToList();
    }

    /// <summary>What a play command came to: the result to return, or why it couldn't be sent.</summary>
    public sealed record PlayOutcome(DeviceCommandResultDto? Result, bool Delivered, string? Error, int Status);

    /// <summary>
    /// Tells a device to play. A show or season is resolved to the episode the device's profile is up to, so the
    /// answer can say what will play; a film, episode or track plays as it is.
    /// </summary>
    public static async Task<PlayOutcome> PlayAsync(string deviceId, DevicePlayRequest request, SignIns signIns, DeviceCommands commands, FlexarrDbContext db,
        PlaybackService playback, CurrentProfile current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(current);
        if (Find(signIns, deviceId) is not { } device)
        {
            return new PlayOutcome(null, false, "No device with that id is signed in.", 404);
        }

        var profileId = device.ProfileId is { } chosen && await db.Profiles.AnyAsync(p => p.Id == chosen, ct) ? chosen : PlaybackState.DefaultUser;
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);
        if (item is null)
        {
            return new PlayOutcome(null, false, "There's no title with that id.", 404);
        }

        var target = await PlayableAsync(item, profileId, db, playback, ct);
        if (target is null)
        {
            return new PlayOutcome(null, false, $"\"{item.Title}\" has nothing that can be played.", 409);
        }

        // Shuffling a show: the device is handed the show itself and asks for random episodes as it goes.
        var shuffle = request.Shuffle && item.Kind is MediaKind.Series or MediaKind.Season;
        var playing = shuffle ? item.Id : target.Id;
        var summary = await db.MediaItems.Where(i => i.Id == playing).Summaries(profileId).FirstAsync(ct);
        var command = new DeviceCommand(DeviceCommand.NewId(), DeviceCommandKind.Play, playing, request.FromBeginning, SenderName(signIns, current), DateTime.UtcNow, shuffle);
        var delivered = commands.Send(device.Id, command);
        return new PlayOutcome(new DeviceCommandResultDto(delivered, device.Name, summary), delivered, null, 0);
    }

    /// <summary>Pause, resume or stop on a device; null when there's no such device.</summary>
    /// <summary>Tells a device to put on a Live TV channel.</summary>
    public static async Task<PlayOutcome> WatchAsync(string deviceId, Guid channelId, SignIns signIns, DeviceCommands commands, FlexarrDbContext db,
        CurrentProfile current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(current);
        if (Find(signIns, deviceId) is not { } device)
        {
            return new PlayOutcome(null, false, null, StatusCodes.Status404NotFound);
        }

        if (await db.LiveChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, ct) is not { } channel)
        {
            return new PlayOutcome(null, false, "There's no such channel.", StatusCodes.Status404NotFound);
        }

        var command = new DeviceCommand(DeviceCommand.NewId(), DeviceCommandKind.Watch, null, false, SenderName(signIns, current), DateTime.UtcNow,
            ChannelId: channel.Id);
        var delivered = commands.Send(device.Id, command);
        return new PlayOutcome(new DeviceCommandResultDto(delivered, device.Name, null, channel.Name), delivered, null, StatusCodes.Status200OK);
    }

    public static DeviceCommandResultDto? Control(string deviceId, DeviceCommandKind kind, SignIns signIns, DeviceCommands commands, CurrentProfile current)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(current);
        if (Find(signIns, deviceId) is not { } device)
        {
            return null;
        }

        var delivered = commands.Send(device.Id, new DeviceCommand(DeviceCommand.NewId(), kind, null, false, SenderName(signIns, current), DateTime.UtcNow));
        return new DeviceCommandResultDto(delivered, device.Name, null);
    }

    public static SignIn? Find(SignIns signIns, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        return signIns.List().FirstOrDefault(s => s.Kind == SignInKind.Device && s.Id == deviceId);
    }

    /// <summary>
    /// The film, episode, track or podcast episode to start for <paramref name="item"/>: itself when it's one; for a
    /// show, the episode <paramref name="profileId"/> is up to; for a season, its first unwatched episode; for an album,
    /// its first track; for a podcast, the episode to hear next.
    /// </summary>
    private static async Task<MediaItem?> PlayableAsync(MediaItem item, string profileId, FlexarrDbContext db, PlaybackService playback, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case MediaKind.Movie or MediaKind.Episode or MediaKind.Track or MediaKind.PodcastEpisode:
                return item;
            case MediaKind.Podcast:
                return await Podcasts.PodcastLibrary.NextToHearAsync(db, item.Id, profileId, ct);
            case MediaKind.Series:
                return await playback.NextUpAsync(item.Id, profileId, ct);
            case MediaKind.Season:
                var episodes = db.MediaItems.AsNoTracking().Where(e => e.ParentId == item.Id && e.Kind == MediaKind.Episode).OrderBy(e => e.SortTitle);
                return await episodes.FirstOrDefaultAsync(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched), ct)
                    ?? await episodes.FirstOrDefaultAsync(ct);
            case MediaKind.Album:
                return await db.MediaItems.AsNoTracking().Where(t => t.ParentId == item.Id && t.Kind == MediaKind.Track)
                    .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.SortTitle).FirstOrDefaultAsync(ct);
            default:
                return null;
        }
    }

    /// <summary>The browser or device asking, by its name in Sign-ins; the API key and the home network have none.</summary>
    private static string? SenderName(SignIns signIns, CurrentProfile current) =>
        current.SignInId is { } id ? signIns.List().FirstOrDefault(s => s.Id == id)?.Name : null;

    /// <summary>
    /// The device's stream of commands, as server-sent events ("event: command", JSON data), with a comment every
    /// twenty seconds so proxies keep it open. Only a device signed in with its own token has one; it ends when a
    /// newer connection from the same device takes over.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, CurrentProfile current, DeviceCommands commands, CancellationToken ct)
    {
        if (http.User.FindFirst(FlexarrClaims.Method)?.Value != ApiKeyAuthenticationHandler.Methods.Device || current.SignInId is not { } deviceId)
        {
            await Results.Problem("Only a device signed in with its own token (an Apple TV, iPhone or iPad connected to this server) can listen for commands.", statusCode: 403)
                .ExecuteAsync(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        using var listener = commands.Listen(deviceId);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                bool more;
                try
                {
                    more = await listener.Commands.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    continue;
                }

                if (!more)
                {
                    break; // A newer connection from this device took over.
                }

                while (listener.Commands.TryRead(out var command))
                {
                    await http.Response.WriteAsync($"event: command\ndata: {JsonSerializer.Serialize(command, Json)}\n\n", ct);
                }

                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The device went away.
        }
    }
}
