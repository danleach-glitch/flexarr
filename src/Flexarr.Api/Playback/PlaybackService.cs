using System.Globalization;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

public sealed class PlaybackService(
    FlexarrDbContext db,
    PlaybackSessions sessions,
    TranscodeManager transcodes,
    MarkerQueue markerQueue,
    TrickplayQueue trickplay,
    SubtitleCache subtitleCache,
    CurrentProfile profile,
    IOptionsMonitor<FlexarrOptions> options)
{
    /// <summary>Within this distance of the end (or once the credits start) an item counts as watched.</summary>
    private const double WatchedThreshold = 0.92;
    private const double MinResumeSeconds = 30;

    public async Task<PlaybackInfoDto?> StartAsync(PlaybackRequest request, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);
        if (request.Shuffle && item is { Kind: MediaKind.Series })
        {
            // Shuffling a show starts a random episode, from the beginning unless asked otherwise.
            item = await ShuffleAsync(item.Id, null, ct);
            request = request with { StartPositionSeconds = request.StartPositionSeconds ?? 0 };
        }

        if (item is null || item.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Track))
        {
            return null;
        }

        var files = await db.MediaFiles.AsNoTracking().Include(f => f.Streams).Include(f => f.Markers)
            .Where(f => f.MediaItemId == item.Id && f.ProbeError == null).ToListAsync(ct);
        var file = files.FirstOrDefault(f => f.Id == request.FileId)
            ?? files.OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        if (file.Streams.Any(s => s.Type == StreamType.Video)
            && Trickplay.Read(PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue).CacheDirectory, file.Id) is null)
        {
            trickplay.Enqueue(file.Id);
        }

        if (file.MarkersScannedAt is null && item.ParentId is { } seasonId && item.Kind == MediaKind.Episode)
        {
            markerQueue.Prioritize(seasonId); // Analyse this season now so skip-intro is ready for the next episode.
        }

        var state = await db.PlaybackStates.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == profile.Id && p.MediaItemId == item.Id, ct);
        var preferences = await LanguagePreferencesAsync(item, ct);
        var subtitles = Subtitles.For(file);
        subtitleCache.Prefetch(file, subtitles);
        var audioStreams = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).ToList();

        var audioIndex = request.AudioStreamIndex
            ?? audioStreams.FirstOrDefault(a => preferences.Audio is not null && a.Language == preferences.Audio)?.Index;
        var subtitleKey = request.SubtitleKey switch
        {
            "off" => null,
            { } key => key,
            null => ChooseSubtitle(subtitles, preferences.Subtitle, audioStreams.FirstOrDefault(a => a.Index == audioIndex)?.Language ?? audioStreams.FirstOrDefault()?.Language),
        };

        var transcoding = options.CurrentValue.Transcoding;
        var maxBitrate = new[] { request.MaxBitrate ?? 0, transcoding.MaxStreamingBitrate }.Where(b => b > 0).DefaultIfEmpty(0).Min();
        var decision = PlaybackDecider.Decide(file, request.Profile, subtitles,
            new PlaybackOptions(audioIndex, subtitleKey, maxBitrate, request.MaxHeight, request.ForceTranscode));

        // The player's own previous session (before an audio switch, a recovery) ends first, so it isn't left encoding
        // ahead for nobody and doesn't count towards the transcode limit. Only that one: the same profile may be
        // watching this title on another screen.
        var restarting = false;
        if (request.ReplacesSessionId is { } replaces)
        {
            if (sessions.Get(replaces) is { } previous && previous.ProfileId == profile.Id)
            {
                await sessions.RemoveAsync(previous.Id);
            }
            else if (transcodes.TakenOverReason(replaces) is { } reason)
            {
                // Its stream was ended for another screen: taking it back is for the viewer to choose, not a recovery.
                throw new PlaybackTakenOverException(reason);
            }
            else
            {
                restarting = true; // The stream it replaces is gone (the server restarted, say).
            }
        }

        var duration = file.DurationSeconds ?? (item.RuntimeMinutes ?? 0) * 60;
        var id = TranscodeManager.NewId();
        TranscodeSession? transcode = null;
        if (decision is { Method: PlaybackMethod.Transcode, Transcode: { } plan })
        {
            transcode = await transcodes.CreateAsync(profile.Id, file.Path, duration, plan, restarting, request.Prepare);
            id = transcode.Id;
        }

        var credits = file.Markers.Where(m => m.Type == MarkerType.Credits).Select(m => (double?)m.StartSeconds).FirstOrDefault();
        sessions.Add(new PlaybackSession
        {
            Id = id,
            ItemId = item.Id,
            ProfileId = profile.Id,
            FileId = file.Id,
            FilePath = file.Path,
            Container = file.Container,
            DurationSeconds = duration,
            Method = decision.Method,
            Subtitles = subtitles,
            CreditsStart = credits,
            Transcode = transcode,
            AudioLanguage = decision.Audio?.Language,
            SubtitleLanguage = decision.Subtitle?.Language,
        });

        var start = request.StartPositionSeconds
            ?? (state is { Watched: false, PositionSeconds: > MinResumeSeconds } && state.PositionSeconds < duration * WatchedThreshold ? state.PositionSeconds : 0);

        if (transcode is not null)
        {
            transcode.StartSeconds = Math.Max(0, start);
        }

        if (request.Prepare && transcode is { } warming)
        {
            // ffmpeg starts with the first segment asked for: ask now for the one playback opens at, so it's ready
            // when the player is.
            var segment = (int)(Math.Max(0, start) / FfmpegCommand.SegmentSeconds);
            _ = Task.Run(async () =>
            {
                try
                {
                    await warming.GetSegmentAsync(segment, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Getting ahead is a nicety: the player asks for the segment itself and sees any real problem.
                }
            }, CancellationToken.None);
        }

        var summary = await db.MediaItems.Where(i => i.Id == item.Id).Summaries(profile.Id).FirstAsync(ct);
        var ancestors = new List<ItemSummaryDto>();
        for (var parentId = item.ParentId; parentId is { } pid;)
        {
            ancestors.Insert(0, await db.MediaItems.Where(i => i.Id == pid).Summaries(profile.Id).FirstAsync(ct));
            parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstAsync(ct);
        }

        var shuffling = request.Shuffle && item.Kind == MediaKind.Episode;
        var watching = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
            .Select(p => new PlaybackPreferencesDto(p.AutoPlayNext, p.SkipIntros, p.SkipRecaps, p.SkipCredits)).FirstOrDefaultAsync(ct);
        var next = shuffling ? await ShuffleNextAsync(item, ct) : await NextEpisodeAsync(item, ct);
        var basePath = $"/api/v1/playback/{id}";
        var burned = decision.Transcode?.BurnSubtitle is not null;

        return new PlaybackInfoDto(
            id,
            decision.Method,
            decision.Method == PlaybackMethod.DirectPlay ? $"{basePath}/stream" : $"{basePath}/master.m3u8",
            decision.Reasons,
            duration,
            Math.Max(0, start),
            file.Id,
            audioStreams.Select(a => new AudioTrackDto(a.Index, Languages.Label(a.Language, a.Title, false), a.Codec, a.Language, a.Channels, a.IsDefault)).ToList(),
            decision.Audio?.Index,
            subtitles.Select(s => new SubtitleTrackDto(s.Key, Languages.Label(s.Language, s.Title, s.IsForced), s.Codec, s.Language, s.IsForced, s.IsDefault, s.IsText,
                s.IsText ? $"{basePath}/subtitles/{s.Key}.vtt" : null)).ToList(),
            decision.Subtitle?.Key,
            burned,
            file.Markers.OrderBy(m => m.StartSeconds).Select(m => new MarkerDto(m.Type, m.StartSeconds, m.EndSeconds)).ToList(),
            summary,
            ancestors,
            next is null ? null : await db.MediaItems.Where(i => i.Id == next.Id).Summaries(profile.Id).FirstAsync(ct),
            decision.Transcode is { } t ? $"{t.TargetHeight}p H.264 at {t.VideoBitrate / 1_000_000.0:0.#} Mbps" + (t.ToneMap ? ", HDR converted to SDR" : "") : null,
            shuffling,
            watching);
    }

    public async Task ReportProgressAsync(PlaybackSession session, ProgressRequest progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == session.ProfileId && p.MediaItemId == session.ItemId, ct);
            var added = state is null;
            if (state is null)
            {
                state = new PlaybackState { UserId = session.ProfileId, MediaItemId = session.ItemId };
                db.PlaybackStates.Add(state);
            }

            var countedPlay = ApplyProgress(state, session, progress);
            try
            {
                await db.SaveChangesAsync(ct);
                session.CountedAsPlay |= countedPlay;
                return;
            }
            catch (DbUpdateException) when (added && attempt == 0)
            {
                // Two first reports for the same item raced and the other inserted the row: apply this one to it.
                db.Entry(state).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Records watching done offline, if it's newer than what's recorded (watching elsewhere since wins). False if
    /// there's no such title.
    /// </summary>
    public async Task<bool> ReportOfflineAsync(Guid itemId, OfflineProgressRequest progress, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return false;
        }

        var playedAt = progress.PlayedAt.ToUniversalTime();
        var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == profile.Id && p.MediaItemId == itemId, ct);
        if (state is not null && state.LastPlayedAt >= playedAt)
        {
            return true;
        }

        if (state is null)
        {
            state = new PlaybackState { UserId = profile.Id, MediaItemId = itemId };
            db.PlaybackStates.Add(state);
        }

        state.LastPlayedAt = playedAt;
        if (progress.Finished)
        {
            if (!state.Watched)
            {
                state.PlayCount++;
            }

            state.Watched = true;
            state.PositionSeconds = 0;
        }
        else if (progress.PositionSeconds > MinResumeSeconds)
        {
            state.PositionSeconds = progress.PositionSeconds;
            state.Watched = false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Applies a progress report to the state; true when it counted a new play.</summary>
    private static bool ApplyProgress(PlaybackState state, PlaybackSession session, ProgressRequest progress)
    {

        var duration = session.DurationSeconds > 0 ? session.DurationSeconds : (double?)null;
        var finished = progress.Ended
            || (duration is { } d && progress.PositionSeconds >= d * WatchedThreshold)
            || (session.CreditsStart is { } credits && progress.PositionSeconds >= credits);

        state.DurationSeconds = duration;
        state.LastPlayedAt = DateTime.UtcNow;
        state.AudioLanguage = session.AudioLanguage ?? state.AudioLanguage;
        state.SubtitleLanguage = session.SubtitleLanguage;

        if (finished)
        {
            state.Watched = true;
            state.PositionSeconds = 0;
            if (!session.CountedAsPlay)
            {
                state.PlayCount++;
                return true;
            }
        }
        else if (progress.PositionSeconds > MinResumeSeconds)
        {
            state.PositionSeconds = progress.PositionSeconds;
            state.Watched = false;
        }

        return false;
    }

    /// <summary>Marks an item (or every episode of a series or season) watched or unwatched.</summary>
    public async Task<bool> SetWatchedAsync(Guid itemId, bool watched, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return false;
        }

        var targets = item.Kind switch
        {
            MediaKind.Series => await db.MediaItems.Where(e => e.Parent!.ParentId == itemId).Select(e => e.Id).ToListAsync(ct),
            MediaKind.Season => await db.MediaItems.Where(e => e.ParentId == itemId).Select(e => e.Id).ToListAsync(ct),
            _ => [itemId],
        };

        var existing = await db.PlaybackStates.Where(p => p.UserId == profile.Id && targets.Contains(p.MediaItemId))
            .ToDictionaryAsync(p => p.MediaItemId, ct);
        foreach (var id in targets)
        {
            if (!existing.TryGetValue(id, out var state))
            {
                if (!watched)
                {
                    continue;
                }

                state = new PlaybackState { UserId = profile.Id, MediaItemId = id };
                db.PlaybackStates.Add(state);
            }
            else if (state.Watched == watched)
            {
                continue; // Already so: keep when it was watched, so history and "last watched" stay true.
            }

            state.Watched = watched;
            state.PositionSeconds = 0;
            state.LastPlayedAt = DateTime.UtcNow;
            if (watched && state.PlayCount == 0)
            {
                state.PlayCount = 1;
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// What "Play" on a series should start: an episode in progress, else the one after the last watched,
    /// else the first unwatched (specials last), else the very first.
    /// </summary>
    public async Task<MediaItem?> NextUpAsync(Guid seriesId, CancellationToken ct)
    {
        var episodes = db.MediaItems.AsNoTracking().Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == seriesId);
        var mine = db.PlaybackStates.AsNoTracking().Where(p => p.UserId == profile.Id && p.MediaItem!.Parent!.ParentId == seriesId);

        var inProgress = await mine.Where(p => !p.Watched && p.PositionSeconds > 0).OrderByDescending(p => p.LastPlayedAt)
            .Select(p => p.MediaItem).FirstOrDefaultAsync(ct);
        if (inProgress is not null)
        {
            return inProgress;
        }

        if (UpNext(await EpisodesAsync(db, profile.Id, [seriesId], ct)) is { } next)
        {
            return await db.MediaItems.AsNoTracking().FirstAsync(e => e.Id == next.Id, ct);
        }

        var ordered = episodes.OrderBy(e => e.SeasonNumber == 0).ThenBy(e => e.SeasonNumber).ThenBy(e => e.SortTitle);
        return await ordered.FirstOrDefaultAsync(e => !e.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched), ct)
            ?? await ordered.FirstOrDefaultAsync(ct);
    }

    /// <summary>The episode after this one: next in the season, else the first of the next season.</summary>
    public async Task<MediaItem?> NextEpisodeAsync(MediaItem episode, CancellationToken ct)
    {
        if (episode.Kind != MediaKind.Episode || episode.ParentId is not { } seasonId
            || await db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.ParentId).FirstOrDefaultAsync(ct) is not { } seriesId)
        {
            return null;
        }

        var episodes = await EpisodesAsync(db, profile.Id, [seriesId], ct);
        return episodes.FirstOrDefault(e => e.Id == episode.Id) is { } current && NextAfter(episodes, current) is { } next
            ? await db.MediaItems.AsNoTracking().FirstAsync(e => e.Id == next.Id, ct)
            : null;
    }

    /// <summary>On shuffle, a random other episode of the show this one is from.</summary>
    private async Task<MediaItem?> ShuffleNextAsync(MediaItem episode, CancellationToken ct) =>
        episode.ParentId is { } seasonId && await db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.ParentId).FirstOrDefaultAsync(ct) is { } seriesId
            ? await ShuffleAsync(seriesId, episode.Id, ct)
            : null;

    /// <summary>A random episode of the show that can be played (one with a readable file), other than <paramref name="current"/>.</summary>
    private async Task<MediaItem?> ShuffleAsync(Guid seriesId, Guid? current, CancellationToken ct)
    {
        var playable = (await db.MediaFiles.AsNoTracking()
            .Where(f => f.ProbeError == null && f.MediaItem!.Parent!.ParentId == seriesId)
            .Select(f => f.MediaItemId).ToListAsync(ct)).ToHashSet();
        var episodes = (await EpisodesAsync(db, profile.Id, [seriesId], ct)).Where(e => playable.Contains(e.Id)).ToList();
        return PickShuffled(episodes, current, Random.Shared, DateTime.UtcNow) is { } pick
            ? await db.MediaItems.AsNoTracking().FirstAsync(e => e.Id == pick.Id, ct)
            : null;
    }

    /// <summary>
    /// The shuffle's pick: any regular episode but the current one (specials only if there's nothing else), leaving
    /// out ones finished in the last day so a short shuffle doesn't come straight back round, unless that's all there is.
    /// </summary>
    internal static EpisodeRef? PickShuffled(IReadOnlyList<EpisodeRef> episodes, Guid? current, Random random, DateTime now)
    {
        var regular = episodes.Where(e => e.SeasonNumber != 0).ToList();
        var pool = (regular.Count > 0 ? regular : episodes).Where(e => e.Id != current).ToList();
        var fresh = pool.Where(e => e.WatchedAt is not { } at || now - at > TimeSpan.FromDays(1)).ToList();
        var from = fresh.Count > 0 ? fresh : pool;
        return from.Count == 0 ? null : from[random.Next(from.Count)];
    }

    /// <summary>What Up Next needs to know about an episode, and when this profile finished it (null if it hasn't).</summary>
    internal sealed record EpisodeRef(
        Guid Id, Guid SeriesId, Guid SeasonId, int? SeasonNumber, int? EpisodeNumber, int? EpisodeNumberEnd, DateOnly? AirDate, string SortTitle, DateTime? WatchedAt);

    /// <summary>Every episode of these shows, in one query.</summary>
    internal static Task<List<EpisodeRef>> EpisodesAsync(FlexarrDbContext db, string profileId, IReadOnlyList<Guid> seriesIds, CancellationToken ct) =>
        db.MediaItems.AsNoTracking()
            .Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId != null && seriesIds.Contains(e.Parent.ParentId.Value))
            .Select(e => new EpisodeRef(e.Id, e.Parent!.ParentId!.Value, e.ParentId!.Value, e.Parent.SeasonNumber, e.EpisodeNumber, e.EpisodeNumberEnd, e.AirDate, e.SortTitle,
                e.PlaybackStates.Where(p => p.UserId == profileId && p.Watched).Select(p => (DateTime?)p.LastPlayedAt).FirstOrDefault()))
            .ToListAsync(ct);

    /// <summary>
    /// The unwatched episode after the last one watched, from one show's episodes. A special watched in the middle
    /// of a run doesn't take over: Up Next carries on from the latest regular episode.
    /// </summary>
    internal static EpisodeRef? UpNext(IReadOnlyList<EpisodeRef> episodes)
    {
        var watched = episodes.Where(e => e.WatchedAt is not null).ToList();
        var last = watched.MaxBy(e => e.WatchedAt);
        if (last?.SeasonNumber == 0)
        {
            last = watched.Where(e => e.SeasonNumber != 0).MaxBy(e => e.WatchedAt) ?? last;
        }

        return last is not null && NextAfter(episodes, last) is { WatchedAt: null } next ? next : null;
    }

    /// <summary>
    /// The episode after <paramref name="current"/>: next in its season, else the first of the next regular season.
    /// Daily shows go by air date, then title order, so two episodes on one day are both seen.
    /// </summary>
    internal static EpisodeRef? NextAfter(IReadOnlyList<EpisodeRef> episodes, EpisodeRef current)
    {
        var season = episodes.Where(e => e.SeasonId == current.SeasonId);
        EpisodeRef? next;
        if (current.AirDate is not null)
        {
            var byDate = season.Where(e => e.AirDate is not null)
                .OrderBy(e => e.AirDate).ThenBy(e => e.SortTitle, StringComparer.Ordinal).ThenBy(e => e.Id).ToList();
            next = byDate.SkipWhile(e => e.Id != current.Id).Skip(1).FirstOrDefault();
        }
        else
        {
            var after = current.EpisodeNumberEnd ?? current.EpisodeNumber ?? 0;
            next = season.Where(e => e.EpisodeNumber > after).OrderBy(e => e.SortTitle, StringComparer.Ordinal).FirstOrDefault();
        }

        return next ?? episodes.Where(e => e.SeasonNumber > current.SeasonNumber && e.SeasonNumber > 0)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.SortTitle, StringComparer.Ordinal).FirstOrDefault();
    }


    public static string MasterPlaylist(PlaybackSession session, bool includeSubtitles)
    {
        var plan = session.Transcode!.Plan;
        var builder = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        var text = includeSubtitles ? session.Subtitles.Where(s => s.IsText).ToList() : [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subtitle in text)
        {
            // NAME must be unique within the group or Apple's players reject the playlist.
            // Titles and languages come from tags inside the file, so they mustn't be able to end the attribute or the line.
            var label = PlaylistText(Languages.Label(subtitle.Language, subtitle.Title, subtitle.IsForced));
            var name = label;
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{label} {n}";
            }

            var language = PlaylistText(subtitle.Language ?? "") is { Length: > 0 } tag ? tag : "und";
            builder.Append(CultureInfo.InvariantCulture,
                $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{name}\",LANGUAGE=\"{language}\",DEFAULT=NO,AUTOSELECT={(subtitle.IsForced ? "YES" : "NO")},FORCED={(subtitle.IsForced ? "YES" : "NO")},URI=\"subtitles/{subtitle.Key}.m3u8\"\n");
        }

        var video = $"avc1.{(plan.TargetHeight > 1080 ? "640033" : "640028")}";
        var codecs = plan.AudioCodecTag is { } audio ? $"{video},{audio}" : video;
        var bandwidth = plan.VideoBitrate + (plan.AudioChannels > 2 ? 384_000 : 192_000);
        builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={plan.TargetWidth}x{plan.TargetHeight},CODECS=\"{codecs}\"{(text.Count > 0 ? ",SUBTITLES=\"subs\"" : "")}\nmain.m3u8\n");
        return builder.ToString();
    }

    public static string SubtitlePlaylist(PlaybackSession session, string key) =>
        string.Create(CultureInfo.InvariantCulture,
            $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:{Math.Ceiling(session.DurationSeconds)}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:{session.DurationSeconds:F3},\n{key}.vtt?hls=true\n#EXT-X-ENDLIST\n");

    /// <summary>Makes text safe inside a quoted playlist attribute: no line breaks or other control characters, no double quotes.</summary>
    private static string PlaylistText(string value) =>
        string.Concat(value.Where(c => !char.IsControl(c)).Select(c => c == '"' ? '\'' : c)).Trim();

    private static string? ChooseSubtitle(IReadOnlyList<SubtitleTrack> subtitles, string? preferredLanguage, string? audioLanguage)
    {
        if (preferredLanguage is not null)
        {
            return (subtitles.FirstOrDefault(s => s.Language == preferredLanguage && !s.IsForced && s.IsText)
                ?? subtitles.FirstOrDefault(s => s.Language == preferredLanguage))?.Key;
        }

        // Forced subtitles translate foreign-language dialogue; show them by default.
        return subtitles.FirstOrDefault(s => s.IsForced && (audioLanguage is null || s.Language is null || s.Language == audioLanguage))?.Key;
    }

    /// <summary>Audio and subtitle languages last used for this item or, for an episode, anywhere in its series.</summary>
    private async Task<(string? Audio, string? Subtitle)> LanguagePreferencesAsync(MediaItem item, CancellationToken ct)
    {
        var related = db.PlaybackStates.AsNoTracking().Where(p => p.UserId == profile.Id);
        related = item.Kind == MediaKind.Episode && item.ParentId is { } seasonId
            ? related.Where(p => p.MediaItem!.Parent!.ParentId == db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.ParentId).First())
            : related.Where(p => p.MediaItemId == item.Id);

        var last = await related.OrderByDescending(p => p.LastPlayedAt).Select(p => new { p.AudioLanguage, p.SubtitleLanguage }).FirstOrDefaultAsync(ct);
        return (last?.AudioLanguage, last?.SubtitleLanguage);
    }
}

/// <summary>A player asked to carry on a stream that was ended to make way for another viewer.</summary>
public sealed class PlaybackTakenOverException(string reason) : Exception(reason);
