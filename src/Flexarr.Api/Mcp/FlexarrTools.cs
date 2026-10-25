using System.ComponentModel;
using Flexarr.Api.Authentication;
using Flexarr.Api.Devices;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Flexarr.Api.Mcp;

/// <summary>A film, show, episode or track as the tools describe it: enough to pick one and say what it is.</summary>
/// <param name="UnwatchedEpisodes">For shows and seasons.</param>
public sealed record McpTitle(Guid Id, MediaKind Kind, string Title, int? Year, string? SeriesTitle, int? SeasonNumber, int? EpisodeNumber,
    bool Watched, double PositionSeconds, double? DurationSeconds, int? UnwatchedEpisodes)
{
    public static McpTitle From(ItemSummaryDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new McpTitle(item.Id, item.Kind, item.Title, item.Year, item.SeriesTitle, item.SeasonNumber, item.EpisodeNumber,
            item.UserData.Watched, item.UserData.PositionSeconds, item.DurationSeconds, item.UserData.UnwatchedCount);
    }
}

public sealed record McpSearchResult(IReadOnlyList<McpTitle> Titles, IReadOnlyList<McpTitle> Episodes);

/// <param name="Online">Flexarr is open on it and would play at once; offline, a command waits two minutes for it.</param>
public sealed record McpDevice(string Id, string Name, bool Online, string ProfileName, McpTitle? NowPlaying, double? PositionSeconds, bool? Paused);

/// <param name="Delivered">False: Flexarr isn't open on the device; the command waits two minutes for it to be opened.</param>
public sealed record McpCommandResult(bool Delivered, string Device, McpTitle? WillPlay, string Message);

public sealed record McpNowPlaying(string Device, string Profile, McpTitle Item, double PositionSeconds, double DurationSeconds, bool Paused);

/// <summary>
/// Flexarr's tools for an AI assistant (Claude, say) connected to <c>/api/v1/mcp</c>: find titles, see what's being
/// watched, and play on an Apple TV, iPhone or iPad with Flexarr open. The assistant signs in like a script, with the
/// API key (or a device token), so it acts as that profile.
/// </summary>
[McpServerToolType]
public static class FlexarrTools
{
    [McpServerTool(Name = "search_library", ReadOnly = true), Description("Finds films, shows and episodes in the library whose title contains the words given. Use the ids with the other tools; a show's id with play_on_device plays its next episode.")]
    public static async Task<McpSearchResult> SearchAsync([Description("Words from the title, e.g. \"traitors\"")] string query, FlexarrDbContext db, CurrentProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var term = (query ?? "").Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (term.Length == 0)
        {
            return new McpSearchResult([], []);
        }

        var contains = $"%{term}%";
        var prefix = $"{term}%";
        var titles = await db.MediaItems
            .Where(i => i.ParentId == null && (EF.Functions.Like(i.Title, contains) || (i.OriginalTitle != null && EF.Functions.Like(i.OriginalTitle, contains))))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix) || EF.Functions.Like(i.SortTitle, prefix))
            .ThenByDescending(i => i.Popularity ?? i.CommunityRating)
            .Take(12).Summaries(profile.Id).ToListAsync(ct);
        var episodes = await db.MediaItems.Where(i => i.Kind == MediaKind.Episode && EF.Functions.Like(i.Title, contains))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).Take(8).Summaries(profile.Id).ToListAsync(ct);
        return new McpSearchResult(titles.Select(McpTitle.From).ToList(), episodes.Select(McpTitle.From).ToList());
    }

    [McpServerTool(Name = "continue_watching", ReadOnly = true), Description("What this profile is in the middle of: films part-watched and the next episode of shows in progress, most recent first.")]
    public static async Task<IReadOnlyList<McpTitle>> ContinueWatchingAsync(FlexarrDbContext db, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        return (await DiscoveryEndpoints.ContinueWatchingAsync(db, profile.Id, 20, null, ct)).Select(McpTitle.From).ToList();
    }

    [McpServerTool(Name = "next_up", ReadOnly = true), Description("The episode this profile should watch next in a show: the one in progress, else the one after the last watched, else the first unwatched. Null when the show has no episodes.")]
    public static async Task<McpTitle?> NextUpAsync([Description("The show's id, from search_library")] Guid seriesId, FlexarrDbContext db, PlaybackService playback, CurrentProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(profile);
        return await playback.NextUpAsync(seriesId, ct) is { } episode
            ? McpTitle.From(await db.MediaItems.Where(i => i.Id == episode.Id).Summaries(profile.Id).FirstAsync(ct))
            : null;
    }

    [McpServerTool(Name = "list_devices", ReadOnly = true), Description("The Apple TVs, iPhones and iPads signed in to this server, whether Flexarr is open on each (online), and what each is playing. Use a device's id with play_on_device and control_device.")]
    public static async Task<IReadOnlyList<McpDevice>> ListDevicesAsync(SignIns signIns, DeviceCommands commands, PlaybackSessions sessions, FlexarrDbContext db,
        CurrentProfile profile, CancellationToken ct)
    {
        var devices = await DeviceEndpoints.ListAsync(signIns, commands, sessions, db, profile, ct);
        return devices.Select(d => new McpDevice(d.Id, d.Name, d.Online, d.ProfileName ?? "Main", d.NowPlaying is { } now ? McpTitle.From(now.Item) : null,
            d.NowPlaying?.PositionSeconds, d.NowPlaying?.Paused)).ToList();
    }

    [McpServerTool(Name = "play_on_device"), Description("Plays a title on a device. Give a film's, episode's or track's id to play that; a show's or season's id plays the episode that device's profile is up to; an album's plays its first track. The result says what will play, and whether the device took it (Flexarr must be open on it; otherwise the command waits two minutes).")]
    public static async Task<McpCommandResult> PlayOnDeviceAsync([Description("The device's id, from list_devices")] string deviceId, [Description("The title's id, from search_library or continue_watching")] Guid itemId,
        [Description("Start from the beginning rather than carry on from where it was left")] bool fromBeginning,
        SignIns signIns, DeviceCommands commands, FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, CancellationToken ct,
        [Description("For a show: play its episodes in a random order")] bool shuffle = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsTrusted)
        {
            throw new InvalidOperationException("This sign-in may not play on other devices.");
        }

        var outcome = await DeviceEndpoints.PlayAsync(deviceId, new DevicePlayRequest(itemId, fromBeginning, shuffle), signIns, commands, db, playback, profile, ct);
        if (outcome.Result is not { } result)
        {
            throw new InvalidOperationException(outcome.Error ?? "Couldn't play that.");
        }

        var what = result.Item is { } item ? Describe(item) : "it";
        return new McpCommandResult(result.Delivered, result.DeviceName, result.Item is { } i ? McpTitle.From(i) : null,
            result.Delivered ? $"{result.DeviceName} is playing {what}." : $"Flexarr isn't open on {result.DeviceName}; {what} will play when it's opened in the next two minutes.");
    }

    [McpServerTool(Name = "control_device"), Description("Pauses, resumes or stops what a device is playing.")]
    public static McpCommandResult ControlDevice([Description("The device's id, from list_devices")] string deviceId, [Description("pause, resume or stop")] string action,
        SignIns signIns, DeviceCommands commands, CurrentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsTrusted)
        {
            throw new InvalidOperationException("This sign-in may not control other devices.");
        }

        var kind = action?.Trim().ToLowerInvariant() switch
        {
            "pause" => DeviceCommandKind.Pause,
            "resume" or "play" => DeviceCommandKind.Resume,
            "stop" => DeviceCommandKind.Stop,
            _ => throw new ArgumentException("action must be pause, resume or stop", nameof(action)),
        };
        var result = DeviceEndpoints.Control(deviceId, kind, signIns, commands, profile) ?? throw new InvalidOperationException("No device with that id is signed in.");
        return new McpCommandResult(result.Delivered, result.DeviceName, null,
            result.Delivered ? $"Sent {kind.ToString().ToLowerInvariant()} to {result.DeviceName}." : $"Flexarr isn't open on {result.DeviceName}, so there's nothing to {kind.ToString().ToLowerInvariant()}.");
    }

    [McpServerTool(Name = "now_playing", ReadOnly = true), Description("Everything playing right now across the household's screens: which device, whose profile, what, and how far in.")]
    public static async Task<IReadOnlyList<McpNowPlaying>> NowPlayingAsync(SignIns signIns, PlaybackSessions sessions, FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(db);
        var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var devices = signIns.List().ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        var list = new List<McpNowPlaying>();
        foreach (var session in sessions.Active.Where(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(15)).OrderByDescending(s => s.LastSeen))
        {
            if (await db.MediaItems.Where(i => i.Id == session.ItemId).Summaries(session.ProfileId).FirstOrDefaultAsync(ct) is { } item)
            {
                list.Add(new McpNowPlaying(session.SignInId is { } id && devices.TryGetValue(id, out var device) ? device : "A browser or script",
                    names.GetValueOrDefault(session.ProfileId, "Main"), McpTitle.From(item), session.LastPosition, session.DurationSeconds, session.Paused));
            }
        }

        return list;
    }

    [McpServerTool(Name = "mark_watched"), Description("Marks a film or episode watched (or unwatched) for this profile; a show or season marks every episode.")]
    public static async Task<string> MarkWatchedAsync([Description("The title's id")] Guid itemId, bool watched, PlaybackService playback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playback);
        return await playback.SetWatchedAsync(itemId, watched, ct) ? (watched ? "Marked watched." : "Marked unwatched.") : throw new InvalidOperationException("There's no title with that id.");
    }

    /// <summary>"The Traitors Uncloaked S3 E4, \"The Reveal\"" or "Heat (1995)".</summary>
    private static string Describe(ItemSummaryDto item) => item.Kind switch
    {
        MediaKind.Episode => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{item.SeriesTitle} S{item.SeasonNumber} E{item.EpisodeNumber}, \"{item.Title}\""),
        MediaKind.Track => $"\"{item.Title}\" by {item.SeriesTitle}",
        _ => item.Year is { } year ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{item.Title} ({year})") : item.Title,
    };
}
