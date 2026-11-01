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

/// <summary>A podcast this profile follows, with how many episodes it hasn't heard.</summary>
public sealed record McpPodcast(Guid Id, string Title, string? Author, int UnplayedEpisodes, DateTime? LatestEpisodeAt);

/// <summary>A podcast episode: its show, when it came out, how long it is and how far this profile got.</summary>
public sealed record McpPodcastEpisode(Guid Id, string Title, string Podcast, Guid PodcastId, DateTime? PublishedAt, double? DurationSeconds,
    double PositionSeconds, bool Played)
{
    public static McpPodcastEpisode From(Podcasts.PodcastEpisodeDto e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new McpPodcastEpisode(e.Id, e.Title, e.PodcastTitle, e.PodcastId, e.PublishedAt, e.DurationSeconds, e.UserData.PositionSeconds, e.UserData.Watched);
    }
}

public sealed record McpNowPlaying(string Device, string Profile, McpTitle Item, double PositionSeconds, double DurationSeconds, bool Paused);

/// <summary>
/// Flexarr's tools for an AI assistant (Claude, say) connected to <c>/api/v1/mcp</c>: find titles, see what's being
/// watched, and play on an Apple TV, iPhone or iPad with Flexarr open. The assistant signs in like a script, with the
/// API key (or a device token), so it acts as that profile.
/// </summary>
[McpServerToolType]
public static class FlexarrTools
{
    [McpServerTool(Name = "search_library", ReadOnly = true), Description("Finds films, shows and episodes in the library whose title contains the words given, forgiving misspellings. Use the ids with the other tools; a show's id with play_on_device plays its next episode.")]
    public static async Task<McpSearchResult> SearchAsync([Description("Words from the title, e.g. \"traitors\"")] string query, FlexarrDbContext db, CurrentProfile profile,
        Search.SearchIndex index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(index);
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
        // Spoken titles come back misspelled ("the traiters"), so top up with near misses.
        titles.AddRange(await index.CloseItemsAsync(db, profile.Id, term, Search.SearchIndex.Group.Title, 12 - titles.Count, titles, ct));
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

    [McpServerTool(Name = "podcasts", ReadOnly = true), Description("The podcasts this profile follows, most recently updated first, with how many episodes it hasn't heard. Narrow it with words from the show's name. A podcast's id with play_on_device plays the episode to hear next.")]
    public static async Task<IReadOnlyList<McpPodcast>> PodcastsAsync(FlexarrDbContext db, CurrentProfile profile, CancellationToken ct,
        [Description("Words from the show's name, e.g. \"rest is history\"; leave out for all")] string? query = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var followed = db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id).Select(s => s.FeedId);
        var shows = db.MediaItems.Where(i => i.Kind == MediaKind.Podcast && followed.Contains(i.Id));
        var term = (query ?? "").Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (term.Length > 0)
        {
            shows = shows.Where(i => EF.Functions.Like(i.Title, $"%{term}%"));
        }

        var me = profile.Id;
        return await shows
            .OrderByDescending(i => i.Children.Max(e => (DateTime?)e.AddedAt)).Take(50)
            .Select(i => new McpPodcast(i.Id, i.Title, i.Studio, i.Children.Count(e => !e.PlaybackStates.Any(p => p.UserId == me && p.Watched)),
                i.Children.Max(e => (DateTime?)e.AddedAt)))
            .ToListAsync(ct);
    }

    [McpServerTool(Name = "new_podcast_episodes", ReadOnly = true), Description("The newest episodes this profile hasn't heard from the podcasts it follows, newest first.")]
    public static async Task<IReadOnlyList<McpPodcastEpisode>> NewPodcastEpisodesAsync(FlexarrDbContext db, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var me = profile.Id;
        var followed = db.PodcastSubscriptions.Where(s => s.ProfileId == me).Select(s => s.FeedId);
        var episodes = db.MediaItems.Where(i => i.Kind == MediaKind.PodcastEpisode && i.ParentId != null && followed.Contains(i.ParentId.Value)
                && !i.PlaybackStates.Any(p => p.UserId == me && p.Watched))
            .OrderByDescending(i => i.AddedAt).Take(20);
        return (await Podcasts.PodcastEndpoints.Episodes(episodes, me, 0).ToListAsync(ct)).Select(McpPodcastEpisode.From).ToList();
    }

    [McpServerTool(Name = "podcast_up_next", ReadOnly = true), Description("This profile's Up Next: the podcast episodes queued to play after the current one, in order, the same on every device.")]
    public static async Task<IReadOnlyList<McpPodcastEpisode>> UpNextAsync(FlexarrDbContext db, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        return (await Podcasts.PodcastEndpoints.Episodes(Podcasts.PodcastEndpoints.UpNext(db, profile.Id), profile.Id, 0).ToListAsync(ct))
            .Select(McpPodcastEpisode.From).ToList();
    }

    [McpServerTool(Name = "add_to_up_next"), Description("Queues a podcast episode in this profile's Up Next, after the rest or (next) straight after what's playing. A podcast's id queues the episode to hear next.")]
    public static async Task<string> AddToUpNextAsync([Description("An episode's id from new_podcast_episodes, or a podcast's from podcasts")] Guid id,
        [Description("Play it next, ahead of the rest")] bool next, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        var episode = item?.Kind switch
        {
            MediaKind.PodcastEpisode => item,
            MediaKind.Podcast => await Podcasts.PodcastLibrary.NextToHearAsync(db, item.Id, profile.Id, ct),
            _ => null,
        };
        if (episode is null)
        {
            throw new InvalidOperationException("That isn't a podcast or podcast episode.");
        }

        var queue = await db.PodcastQueue.Where(q => q.ProfileId == profile.Id).OrderBy(q => q.Position).ToListAsync(ct);
        var entry = queue.FirstOrDefault(q => q.EpisodeId == episode.Id) ?? db.PodcastQueue.Add(new Core.Podcasts.PodcastQueueEntry { ProfileId = profile.Id, EpisodeId = episode.Id }).Entity;
        queue.Remove(entry);
        queue.Insert(next ? 0 : queue.Count, entry);
        for (var i = 0; i < queue.Count; i++)
        {
            queue[i].Position = i;
        }

        await db.SaveChangesAsync(ct);
        return $"“{episode.Title}” is {(next ? "next" : $"number {queue.Count}")} in Up Next.";
    }

    [McpServerTool(Name = "audiobooks_in_progress", ReadOnly = true), Description("The audiobooks this profile is part way through, most recently heard first, with how far in. A book's id with play_on_device resumes it.")]
    public static async Task<IReadOnlyList<McpTitle>> AudiobooksInProgressAsync(FlexarrDbContext db, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var books = db.MediaItems.Where(i => i.Kind == MediaKind.Audiobook && i.MissingSince == null);
        return [.. (await Books.BookQueries.InProgress(books, profile.Id)
            .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profile.Id).Select(p => p.LastPlayedAt).FirstOrDefault())
            .Take(10).Summaries(profile.Id).ToListAsync(ct)).Select(McpTitle.From)];
    }

    [McpServerTool(Name = "set_sleep_timer"), Description("Sets the sleep timer on a device that's playing: stop after some minutes, or at the end of the audiobook's chapter. Neither turns it off. Flexarr must be open on the device.")]
    public static McpCommandResult SetSleepTimer([Description("The device's id, from list_devices")] string deviceId, SignIns signIns, DeviceCommands commands, CurrentProfile profile,
        [Description("Minutes from now, 1 to 600")] double? minutes = null, [Description("Stop at the end of the chapter instead")] bool endOfChapter = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsTrusted)
        {
            throw new InvalidOperationException("This sign-in may not control other devices.");
        }

        var result = DeviceEndpoints.Sleep(deviceId, new DeviceSleepRequest(minutes, endOfChapter), signIns, commands, profile);
        if (result is not Microsoft.AspNetCore.Http.HttpResults.Ok<DeviceCommandResultDto> { Value: { } sent })
        {
            throw new InvalidOperationException(result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem ? problem.ProblemDetails.Detail : "No device with that id is signed in.");
        }

        var what = endOfChapter ? "at the end of the chapter" : minutes is { } m ? $"in {m:0} minutes" : null;
        return new McpCommandResult(true, sent.DeviceName, null, what is null ? $"Turned off the sleep timer on {sent.DeviceName}." : $"{sent.DeviceName} will stop {what}.");
    }

    [McpServerTool(Name = "play_on_device"), Description("Plays a title on a device. Give a film's, episode's, track's, podcast episode's or audiobook's id to play that (a book carries on from where it was left); a show's or season's id plays the episode that device's profile is up to; an album's plays its first track; a podcast's plays the episode to hear next. The result says what will play, and whether the device took it (Flexarr must be open on it; otherwise the command waits two minutes).")]
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

    [McpServerTool(Name = "set_device_sound"), Description("Changes a device's sound: dialogue boost (makes voices clearer), night mode (quietens loud scenes) and speakers. Only reaches a device with Flexarr open; anything left out stays as it is.")]
    public static McpCommandResult SetDeviceSound([Description("The device's id, from list_devices")] string deviceId,
        SignIns signIns, DeviceCommands commands, CurrentProfile profile,
        [Description("off, low, medium or high")] string? dialogueBoost = null, [Description("off, on, or scheduled (the hours set on the device)")] string? nightMode = null,
        [Description("auto, stereo or surround (5.1)")] string? speakers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsTrusted)
        {
            throw new InvalidOperationException("This sign-in may not control other devices.");
        }

        // By name only: Enum.TryParse would also take "1" or "low, high".
        static T? Parse<T>(string? value, string name) where T : struct, Enum =>
            string.IsNullOrWhiteSpace(value) ? null
            : Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) is { } match ? Enum.Parse<T>(match)
            : throw new ArgumentException($"{name} must be {string.Join(", ", Enum.GetNames<T>()).ToLowerInvariant()}", name);
        var sound = new DeviceSound(Parse<DialogueBoost>(dialogueBoost, nameof(dialogueBoost)), Parse<NightMode>(nightMode, nameof(nightMode)), Parse<Speakers>(speakers, nameof(speakers)));
        if (sound.IsEmpty)
        {
            throw new ArgumentException("Say what to change: dialogueBoost, nightMode or speakers.");
        }

        var result = DeviceEndpoints.SetSound(deviceId, sound, signIns, commands, profile) ?? throw new InvalidOperationException("No device with that id is signed in.");
        return new McpCommandResult(result.Delivered, result.DeviceName, null,
            result.Delivered ? $"Sent to {result.DeviceName}."
            : commands.IsOnline(deviceId) ? $"The Flexarr app on {result.DeviceName} is too old to change its sound, so nothing was sent."
            : $"Flexarr isn't open on {result.DeviceName}, so nothing was sent.");
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
            if (await LibraryVisibility.PlayingAsync(db, session.ItemId, session.ProfileId, ct) is { } item)
            {
                list.Add(new McpNowPlaying(session.SignInId is { } id && devices.TryGetValue(id, out var device) ? device : "A browser or script",
                    names.GetValueOrDefault(session.ProfileId, "Main"), McpTitle.From(item), session.LastPosition,
                    item.Id == Guid.Empty ? 0 : session.DurationSeconds, session.Paused)); // a private title's length could say which it is
            }
        }

        return list;
    }

    [McpServerTool(Name = "mark_watched"), Description("Marks a film, episode or audiobook watched (or unwatched) for this profile; a show or season marks every episode. Unwatched also forgets how far in it was, taking it out of Continue watching.")]
    public static async Task<string> MarkWatchedAsync([Description("The title's id")] Guid itemId, bool watched, PlaybackService playback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playback);
        return await playback.SetWatchedAsync(itemId, watched, ct) ? (watched ? "Marked watched." : "Marked unwatched.") : throw new InvalidOperationException("There's no title with that id.");
    }

    [McpServerTool(Name = "remove_from_continue_watching"), Description("Takes a film or episode out of this profile's Continue watching without marking it watched: forgets how far in it was and, for an episode, keeps its show's next episode out of the row until another is played.")]
    public static async Task<string> RemoveFromContinueWatchingAsync([Description("The title's id")] Guid itemId, PlaybackService playback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playback);
        return await playback.RemoveFromContinueWatchingAsync(itemId, ct) ? "Removed from Continue watching." : throw new InvalidOperationException("There's no title with that id.");
    }

    /// <summary>"The Traitors Uncloaked S3 E4, \"The Reveal\"" or "Heat (1995)".</summary>
    private static string Describe(ItemSummaryDto item) => item.Kind switch
    {
        MediaKind.Episode => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{item.SeriesTitle} S{item.SeasonNumber} E{item.EpisodeNumber}, \"{item.Title}\""),
        MediaKind.Track => $"\"{item.Title}\" by {item.SeriesTitle}",
        MediaKind.PodcastEpisode => $"{item.SeriesTitle}: \"{item.Title}\"",
        _ => item.Year is { } year ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{item.Title} ({year})") : item.Title,
    };
}
