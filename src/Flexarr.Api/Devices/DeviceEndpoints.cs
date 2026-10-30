using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Notifications;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Devices;

/// <param name="Paused">Paused on the device (as it last reported).</param>
/// <param name="ReportedAt">When the device said where it was: playing, it's further on by now.</param>
/// <param name="Rate">The speed it's playing at. This and the rest are absent from devices that don't tell remotes (apps before 0.56).</param>
/// <param name="Prompt">The button over the picture (Skip Intro, Next Episode); absent, none.</param>
/// <param name="HasNext">There's a next episode to move on to.</param>
/// <param name="Tracks">Its soundtracks and subtitles.</param>
/// <param name="Sound">Its sound settings.</param>
/// <param name="AgeSeconds">How long ago <paramref name="ReportedAt"/> was, by the server's clock, for a device whose own clock differs.</param>
public sealed record NowPlayingDto(ItemSummaryDto Item, double PositionSeconds, double DurationSeconds, bool Paused, DateTime? ReportedAt = null,
    double? Rate = null, PlayerPrompt? Prompt = null, bool? HasNext = null, PlayerTracks? Tracks = null, DeviceSound? Sound = null, double? AgeSeconds = null);

/// <summary>An Apple TV, iPhone, iPad or Android device signed in to this server, and whether it would take a command right now.</summary>
/// <param name="Online">Flexarr is open on it and listening. Offline, a command waits a couple of minutes for it.</param>
/// <param name="ProfileId">Whose history it plays against: the profile chosen on it.</param>
/// <param name="IsThisOne">The device asking.</param>
/// <param name="DeviceType">What it is, to name and draw it; absent until its app says (apps from before 0.54 don't).</param>
/// <param name="Accepts">The commands it takes, for a remote to offer only those; absent when its app doesn't say (before 0.56: play, pause, resume, stop, watch and sound).</param>
public sealed record DeviceDto(string Id, string Name, bool Online, DateTime LastUsedAt, string ProfileId, string? ProfileName, bool IsThisOne, NowPlayingDto? NowPlaying,
    DeviceType? DeviceType = null, IReadOnlyList<DeviceCommandKind>? Accepts = null);

public sealed record DeviceSeekRequest(double PositionSeconds);

public sealed record DeviceSkipRequest(double Seconds);

/// <param name="TrackId">The player's id for the track (from <see cref="NowPlayingDto.Tracks"/>); absent, for subtitles, turns them off.</param>
public sealed record DeviceTrackRequest(string? TrackId);

public sealed record DeviceRateRequest(double Rate);

/// <param name="ItemId">A film, episode or track to play, or a show, season or album: the next episode (or the first track) is worked out for the device's profile.</param>
/// <param name="FromBeginning">Start over rather than resume.</param>
/// <param name="Shuffle">For a show: its episodes in a random order, one after another.</param>
/// <param name="ProfileId">Whose to play it as: the device switches to that profile first (asking for its PIN, if it has one). Absent: the device's own.</param>
/// <param name="StartSeconds">Where to start a film, episode or track (Undo after Stop); absent, where it was left. Apps before 0.56 ignore it.</param>
public sealed record DevicePlayRequest(Guid ItemId, bool FromBeginning = false, bool Shuffle = false, string? ProfileId = null, double? StartSeconds = null);

/// <param name="Delivered">The device took the command. False: Flexarr isn't open on it; the command waits a couple of minutes for it to be.</param>
/// <param name="Item">For play: what will play (the episode, when a show was asked for).</param>
/// <param name="Channel">For watch: the channel being put on.</param>
public sealed record DeviceCommandResultDto(bool Delivered, string DeviceName, ItemSummaryDto? Item, string? Channel = null);

public sealed record DeviceWatchRequest(Guid ChannelId);

/// <summary>
/// Devices as targets: list them, tell one to play something (or pause, resume, stop, or change its sound), and the stream a device keeps
/// open to hear such commands. How the phone (and Siri, and an assistant over MCP) plays on the TV.
/// </summary>
public static class DeviceEndpoints
{
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

        // Night mode on for the living room, from an assistant or an automation: only to a device with Flexarr open.
        group.MapPost("/{id}/sound", (string id, DeviceSound request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
        {
            if (!current.IsTrusted)
            {
                return Forbidden();
            }

            if (request.IsEmpty)
            {
                return Results.Problem("Say what to change: dialogueBoost, nightMode or speakers.", statusCode: StatusCodes.Status400BadRequest);
            }

            return SetSound(id, request, signIns, commands, current) switch
            {
                null => Results.NotFound(),
                { Delivered: true } result => Results.Ok(result),
                { } result when commands.IsOnline(id) => Results.Problem($"The Flexarr app on {result.DeviceName} can't do that yet: update it.",
                    statusCode: StatusCodes.Status409Conflict),
                { } result => Results.Problem($"Flexarr isn't open on {result.DeviceName}.", statusCode: StatusCodes.Status409Conflict),
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

        // A remote on another device: where to go, how far to jump, the next episode, a soundtrack. Only to a device with
        // Flexarr open (a seek a couple of minutes late means nothing) whose app takes it.
        group.MapPost("/{id}/seek", (string id, DeviceSeekRequest request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            double.IsFinite(request.PositionSeconds) && request.PositionSeconds >= 0
                ? Remote(id, DeviceCommandKind.Seek, signIns, commands, current, seconds: request.PositionSeconds)
                : Results.Problem("Say where to go, in seconds from the start.", statusCode: StatusCodes.Status400BadRequest));
        group.MapPost("/{id}/skip", (string id, DeviceSkipRequest request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            double.IsFinite(request.Seconds) && request.Seconds != 0
                ? Remote(id, DeviceCommandKind.Skip, signIns, commands, current, seconds: request.Seconds)
                : Results.Problem("Say how many seconds to jump: negative goes back.", statusCode: StatusCodes.Status400BadRequest));
        group.MapPost("/{id}/audio", (string id, DeviceTrackRequest request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            request.TrackId is not { Length: > 0 and <= MaxTrackId }
                ? Results.Problem("Say which soundtrack.", statusCode: StatusCodes.Status400BadRequest)
                : Remote(id, DeviceCommandKind.AudioTrack, signIns, commands, current, trackId: request.TrackId));
        group.MapPost("/{id}/subtitles", (string id, DeviceTrackRequest request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            request.TrackId is { Length: > MaxTrackId }
                ? Results.Problem("That isn't one of the player's subtitles.", statusCode: StatusCodes.Status400BadRequest)
                : Remote(id, DeviceCommandKind.SubtitleTrack, signIns, commands, current, trackId: string.IsNullOrEmpty(request.TrackId) ? null : request.TrackId));
        group.MapPost("/{id}/rate", (string id, DeviceRateRequest request, SignIns signIns, DeviceCommands commands, CurrentProfile current) =>
            request.Rate is >= 0.5 and <= 2
                ? Remote(id, DeviceCommandKind.Rate, signIns, commands, current, rate: request.Rate)
                : Results.Problem("The speed is between 0.5 and 2.", statusCode: StatusCodes.Status400BadRequest));
        foreach (var (path, kind) in new[] { ("next", DeviceCommandKind.Next), ("previous", DeviceCommandKind.Previous), ("skip-segment", DeviceCommandKind.SkipSegment) })
        {
            group.MapPost($"/{{id}}/{path}", (string id, SignIns signIns, DeviceCommands commands, CurrentProfile current) => Remote(id, kind, signIns, commands, current));
        }

        // A phone's Live Activity for what a TV is playing: kept up to date through the push relay while the phone is locked.
        group.MapPut("/{id}/activity", (string id, LiveActivityRequest request, SignIns signIns, LiveActivities activities, PushRelay relay, CurrentProfile current,
            HttpContext http) =>
        {
            if (!current.IsTrusted)
            {
                return Forbidden();
            }

            if (Find(signIns, id) is null)
            {
                return Results.NotFound();
            }

            if (!LiveActivities.IsToken(request.Token) || request.Environment is not ("production" or "sandbox") || request.ItemId == Guid.Empty)
            {
                return Results.Problem("Send the activity's push token (hex) and environment (production or sandbox).", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!relay.IsEnabled)
            {
                return Results.Problem("Notifications are turned off on this server.", statusCode: StatusCodes.Status409Conflict);
            }

            return activities.Start(id, request, Owner(current, http))
                ? Results.NoContent()
                : Results.Problem("Too many Live Activities.", statusCode: StatusCodes.Status429TooManyRequests);
        });
        group.MapDelete("/{id}/activity/{token}", (string id, string token, LiveActivities activities, CurrentProfile current, HttpContext http) =>
        {
            if (!current.IsTrusted)
            {
                return Forbidden();
            }

            // A phone ends its own; the API key itself any.
            activities.End(token, id, Owner(current, http));
            return Results.NoContent();
        });

        // What a device keeps open while Flexarr is in front: commands arrive as server-sent events.
        group.MapGet("/commands", StreamAsync);

        return api;
    }

    /// <summary>Sends a remote's command to a device that's listening and takes it: 409 when it isn't open, or its app is too old.</summary>
    private static IResult Remote(string deviceId, DeviceCommandKind kind, SignIns signIns, DeviceCommands commands, CurrentProfile current, double? seconds = null,
        string? trackId = null, double? rate = null)
    {
        if (!current.IsTrusted)
        {
            return Forbidden();
        }

        if (Find(signIns, deviceId) is not { } device)
        {
            return Results.NotFound();
        }

        // Not open first: what a device takes is only known once it has listened since the server started.
        if (!commands.IsOnline(device.Id))
        {
            return Results.Problem($"Flexarr isn't open on {device.Name}.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!commands.Takes(device.Id, kind))
        {
            return Results.Problem($"The Flexarr app on {device.Name} can't do that yet: update it.", statusCode: StatusCodes.Status409Conflict);
        }

        var command = new DeviceCommand(DeviceCommand.NewId(), kind, null, false, SenderName(signIns, current), DateTime.UtcNow, Seconds: seconds, TrackId: trackId,
            Rate: rate);
        return commands.Send(device.Id, command, keep: false)
            ? Results.Ok(new DeviceCommandResultDto(true, device.Name, null))
            : Results.Problem($"Flexarr isn't open on {device.Name}.", statusCode: StatusCodes.Status409Conflict);
    }

    private const int MaxTrackId = 64;

    /// <summary>Who a Live Activity belongs to: the sign-in, or for the API key itself null (it may end any); any other way in (the home network, Tailscale) by its method.</summary>
    private static string? Owner(CurrentProfile current, HttpContext http) =>
        current.SignInId ?? (http.User.FindFirst(FlexarrClaims.Method)?.Value is { } method and not ApiKeyAuthenticationHandler.Methods.ApiKey ? $"{method}:{current.Id}" : null);

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
            var nowPlaying = await NowPlayingAsync(device.Id, sessions, db, ct);
            var accepts = commands.Accepts(device.Id) is { } extra ? DeviceCommand.Basic.Union(extra).Order().ToList() : null;
            list.Add(new DeviceDto(device.Id, device.Name, commands.IsOnline(device.Id), device.LastUsedAt, profileId, names.GetValueOrDefault(profileId),
                device.Id == current.SignInId, nowPlaying, device.DeviceType, accepts));
        }

        return list.OrderByDescending(d => d.Online).ThenByDescending(d => d.LastUsedAt).ToList();
    }

    /// <summary>
    /// What a device is playing, as it last reported; null when nothing (or nothing lately: playing but silent for a while,
    /// the app killed or the TV asleep, is over). A title from a library hidden from this context's profile shows as
    /// playing, but not what it is. With <paramref name="cached"/> (the feed, which no profile asks for: it hides per
    /// listener) the title is looked up once per playback.
    /// </summary>
    public static async Task<NowPlayingDto?> NowPlayingAsync(string deviceId, PlaybackSessions sessions, FlexarrDbContext db, CancellationToken ct, bool cached = false)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(db);
        var now = DateTime.UtcNow;
        var playing = sessions.Active.Where(s => s.SignInId == deviceId && PlaybackSessions.Shows(s, now)).OrderByDescending(s => s.LastSeen).FirstOrDefault();
        if (playing is null)
        {
            return null;
        }

        var item = cached && playing.Summary is { } kept ? kept : await LibraryVisibility.PlayingAsync(db, playing.ItemId, playing.ProfileId, ct);
        if (item is null)
        {
            return null;
        }

        if (cached)
        {
            playing.Summary = item;
        }

        var remote = playing.Remote;
        var dto = new NowPlayingDto(item, playing.LastPosition, playing.DurationSeconds, playing.Paused, playing.ReportedAt, remote?.Rate, remote?.Prompt, remote?.HasNext,
            remote?.Tracks, remote?.Sound, playing.ReportedAt is { } at ? Math.Max(0, (now - at).TotalSeconds) : null);
        return item.Id == Guid.Empty ? Private(dto) : dto;
    }

    /// <summary>
    /// A title the listener mayn't see, told as playing and nothing more: its tracks, the button over the picture and
    /// its length could say what it is as surely as its name.
    /// </summary>
    private static NowPlayingDto Private(NowPlayingDto playing) =>
        playing with { Item = playing.Item.Id == Guid.Empty ? playing.Item : playing.Item.Private(), DurationSeconds = 0, Tracks = null, Prompt = null, HasNext = null, Sound = null };

    /// <summary>How often a device's stream looks again at which libraries its profile hides (tests make it every time).</summary>
    internal static TimeSpan HiddenRecheck { get; set; } = TimeSpan.FromSeconds(5);

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

        if (request.ProfileId is { } asked && !await db.Profiles.AnyAsync(p => p.Id == asked, ct))
        {
            return new PlayOutcome(null, false, "There's no profile with that id.", 404);
        }

        // The profile asked for, or the one chosen on the device: the next episode is theirs.
        var profileId = request.ProfileId ?? (device.ProfileId is { } chosen && await db.Profiles.AnyAsync(p => p.Id == chosen, ct) ? chosen : PlaybackState.DefaultUser);
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
        var start = !shuffle && playing == item.Id && request.StartSeconds is { } at && double.IsFinite(at) && at >= 0 ? at : (double?)null;
        var command = new DeviceCommand(DeviceCommand.NewId(), DeviceCommandKind.Play, playing, request.FromBeginning, SenderName(signIns, current), DateTime.UtcNow, shuffle,
            ProfileId: request.ProfileId, Seconds: start);
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

    /// <summary>
    /// Sends <paramref name="sound"/> to a device that's listening and takes it (apps from 0.56 say so); it isn't kept for
    /// one that isn't (null: no such device; not delivered and online: its app is too old).
    /// </summary>
    public static DeviceCommandResultDto? SetSound(string deviceId, DeviceSound sound, SignIns signIns, DeviceCommands commands, CurrentProfile current)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(current);
        if (Find(signIns, deviceId) is not { } device)
        {
            return null;
        }

        if (!commands.Takes(device.Id, DeviceCommandKind.Sound))
        {
            return new DeviceCommandResultDto(false, device.Name, null);
        }

        var command = new DeviceCommand(DeviceCommand.NewId(), DeviceCommandKind.Sound, null, false, SenderName(signIns, current), DateTime.UtcNow, Sound: sound);
        return new DeviceCommandResultDto(commands.Send(device.Id, command, keep: false), device.Name, null);
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
    /// newer connection from the same device takes over. On the main profile it also carries devices asking to
    /// connect ("event: pairing", <see cref="PairingDetailsDto"/>; every one waiting is sent first) and those done
    /// with ("event: pairing-ended", the code), so the phone can ask "Allow?" while Flexarr is open on it. Apps from
    /// before these ignore events they don't know. <c>accepts</c> lists the commands beyond the basic ones the device
    /// takes (Seek,Skip,…); with <c>nowPlaying=true</c> the stream also carries what every device is playing as it
    /// changes ("event: now-playing", <see cref="DeviceNowPlayingDto"/>), for a remote.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, CurrentProfile current, DeviceCommands commands, Pairings pairings, NowPlayingFeed feed,
        FlexarrDbContext db, SignIns signIns, string? accepts, bool? nowPlaying, CancellationToken ct)
    {
        // Titles from libraries hidden from this device's profile are told as playing, but not what they are. Looked up
        // again every few seconds: the profile chosen on the device, or what it hides, can change while it listens.
        var hidden = db.HiddenLibraryIds.ToHashSet();
        var hiddenAt = DateTime.UtcNow;
        async Task<HashSet<Guid>> HiddenAsync()
        {
            if (DateTime.UtcNow - hiddenAt < HiddenRecheck || current.SignInId is not { } signInId)
            {
                return hidden;
            }

            var profile = signIns.List().FirstOrDefault(s => s.Id == signInId)?.ProfileId ?? PlaybackState.DefaultUser;
            var ids = await db.Profiles.AsNoTracking().Where(p => p.Id == profile).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct);
            hidden = (ids ?? []).ToHashSet();
            hiddenAt = DateTime.UtcNow;
            return hidden;
        }

        if (http.User.FindFirst(FlexarrClaims.Method)?.Value != ApiKeyAuthenticationHandler.Methods.Device || current.SignInId is not { } deviceId)
        {
            await Results.Problem("Only a device signed in with its own token (an Apple TV, iPhone or iPad connected to this server) can listen for commands.", statusCode: 403)
                .ExecuteAsync(http);
            return;
        }

        // Listening before saying so: once the device hears "connected", it's online and commands reach it. Names this
        // server doesn't know (from a newer app) are left out.
        var takes = accepts?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Enum.TryParse<DeviceCommandKind>(name, ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : (DeviceCommandKind?)null)
            .OfType<DeviceCommandKind>().ToList();
        using var listener = commands.Listen(deviceId, takes);
        using var asking = current.IsAdmin || current.CanUnlock ? pairings.Listen() : null;
        using var watching = nowPlaying == true ? feed.Listen() : null;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                bool more;
                try
                {
                    var command = listener.Commands.WaitToReadAsync(wait.Token).AsTask();
                    var others = new List<Task<bool>>(2);
                    if (asking is not null)
                    {
                        others.Add(asking.Notices.WaitToReadAsync(wait.Token).AsTask());
                    }

                    if (watching is not null)
                    {
                        others.Add(watching.Changes.WaitToReadAsync(wait.Token).AsTask());
                    }

                    if (others.Count == 0)
                    {
                        more = await command;
                    }
                    else
                    {
                        var first = await Task.WhenAny([command, .. others]);
                        more = await first; // the heartbeat's timeout cancels them all: a ping, below
                        await wait.CancelAsync(); // the other waits
                        if (first != command && command.IsCompletedSuccessfully)
                        {
                            more = command.Result;
                        }
                    }
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

                while (asking is not null && asking.Notices.TryRead(out var notice))
                {
                    await http.Response.WriteAsync(notice.Waiting is { } waiting
                        ? $"event: pairing\ndata: {JsonSerializer.Serialize(waiting, Json)}\n\n"
                        : $"event: pairing-ended\ndata: {JsonSerializer.Serialize(new { code = notice.Ended }, Json)}\n\n", ct);
                }

                while (watching is not null && watching.Changes.TryRead(out var change))
                {
                    if (change.NowPlaying is { Item: var item } playing && (await HiddenAsync()).Contains(item.LibraryId))
                    {
                        change = change with { NowPlaying = Private(playing) };
                    }

                    await http.Response.WriteAsync($"event: now-playing\ndata: {JsonSerializer.Serialize(change, Json)}\n\n", ct);
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
