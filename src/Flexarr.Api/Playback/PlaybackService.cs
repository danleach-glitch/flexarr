using System.Globalization;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;
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
    RemuxIndex remuxIndex,
    CurrentProfile profile,
    IOptionsMonitor<FlexarrOptions> options,
    Security.GuestLinks guestLinks,
    Authentication.SignIns? signIns = null,
    Books.BookFileConverter? bookConverter = null)
{
    /// <summary>Within this distance of the end (or once the credits start) an item counts as watched.</summary>
    internal const double WatchedThreshold = 0.92;
    private const double MinResumeSeconds = 30;

    /// <summary>A copied Dolby track whose first frame is later than this into the file is converted (see StartAsync).</summary>
    private const double LateAudioSeconds = 0.5;

    public async Task<PlaybackInfoDto?> StartAsync(PlaybackRequest request, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);
        if (request.Shuffle && item is { Kind: MediaKind.Series })
        {
            // Shuffling a show starts a random episode, from the beginning unless asked otherwise.
            item = await ShuffleAsync(item.Id, null, ct);
            request = request with { StartPositionSeconds = request.StartPositionSeconds ?? 0 };
        }

        if (item is { Kind: MediaKind.Audiobook })
        {
            return await StartBookAsync(item, request, ct);
        }

        if (item is null || item.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Track or MediaKind.PodcastEpisode))
        {
            return null;
        }

        var files = await db.MediaFiles.AsNoTracking().Include(f => f.Streams).Include(f => f.Markers)
            .Where(f => f.MediaItemId == item.Id && f.ProbeError == null).ToListAsync(ct);
        var file = files.FirstOrDefault(f => f.Id == request.FileId)
            ?? files.OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefault();
        if (file is null)
        {
            return item.Kind == MediaKind.PodcastEpisode ? await StartPodcastAsync(item, request, ct) : null;
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
        var subtitles = Subtitles.For(file, FfmpegFilters.HasDecoder(PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue).FfmpegPath, "libzvbi_teletextdec"));
        if (!request.Profile.SelectsTracksItself)
        {
            // Teletext this server can't read can't be converted or burned in either; only a player that decodes it can show it.
            subtitles = subtitles.Where(s => !s.IsTeletext || s.IsText).ToList();
        }

        subtitleCache.Prefetch(file, subtitles);
        var audioStreams = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).ToList();

        var audioIndex = request.AudioStreamIndex
            ?? AudioTracks.InLanguage(audioStreams, preferences.Audio)?.Index;
        var subtitleKey = request.SubtitleKey switch
        {
            "off" => null,
            { } key => key,
            null => ChooseSubtitle(subtitles, preferences.Subtitle, audioStreams.FirstOrDefault(a => a.Index == audioIndex)?.Language ?? audioStreams.FirstOrDefault()?.Language),
        };

        var transcoding = options.CurrentValue.Transcoding;
        // A family guest on Tailscale's slow shared relays is held to what fits through them.
        var slowLink = await guestLinks.CapAsync(ct) ?? 0;
        var maxBitrate = new[] { request.MaxBitrate ?? 0, transcoding.MaxStreamingBitrate, slowLink }.Where(b => b > 0).DefaultIfEmpty(0).Min();
        var playbackOptions = new PlaybackOptions(audioIndex, subtitleKey, maxBitrate, request.MaxHeight, request.ForceTranscode, file.AudioDelaySeconds,
            request.Sound is { } sound ? sound with { MaxChannels = sound.MaxChannels is { } max ? Math.Clamp(max, 1, 8) : null } : null);
        var decision = PlaybackDecider.Decide(file, request.Profile, subtitles, playbackOptions);

        // A player that takes fragmented MP4 (Apple's) gets the video as it is when only the container or the sound was
        // in the way: the original picture, HDR and Dolby Vision included. It needs the file's keyframes, which an MKV
        // indexes; others are scanned in the background and repackaged from next time.
        if (decision.Transcode is { } converting && request.Profile.Fmp4 && file.VideoCodec is "h264" or "hevc"
            && await remuxIndex.GetAsync(file.Path, ct) is { } index
            && PlaybackDecider.Repackaged(converting, file, request.Profile, playbackOptions, index) is { } repackaged)
        {
            decision = decision with { Transcode = repackaged };
        }

        // Fragmented MP4 describes Dolby Digital (Plus) from its first frame, so a track that starts well after the
        // picture (a silent opening) leaves the stream's init segment without it, and an Apple TV refuses the stream.
        // That track is converted instead, which needs no frame to describe.
        if (decision.Transcode is { Fmp4: true, CopyAudio: true, SourceAudioCodec: "ac3" or "eac3", AudioStreamIndex: { } audioStream } copying
            && await remuxIndex.FirstPacketSecondsAsync(file.Path, audioStream, ct) > LateAudioSeconds)
        {
            decision = decision with { Transcode = copying with { CopyAudio = false } };
        }

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
            SignInId = profile.SignInId,
            FileId = file.Id,
            FilePath = file.Path,
            Container = file.Container,
            DurationSeconds = duration,
            Method = decision.Method,
            Subtitles = subtitles,
            CreditsStart = credits,
            Transcode = transcode,
            AudioLanguage = decision.Audio?.Language,
            // Forced subtitles aren't a preference (the server puts them on by itself, and players carry the choice
            // over to the next episode): remembering their language would turn the full subtitles on next time.
            SubtitleLanguage = decision.Subtitle is { IsForced: true } ? null : decision.Subtitle?.Language,
            AudioIsAtmos = decision.Audio?.Profile?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true,
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
            var segment = warming.SegmentAt(start);
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
            // A scan may take a parent away mid-request: the trail stops there instead of failing.
            if (await db.MediaItems.Where(i => i.Id == pid).Summaries(profile.Id).FirstOrDefaultAsync(ct) is not { } parent)
            {
                break;
            }

            ancestors.Insert(0, parent);
            parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
        }

        var shuffling = request.Shuffle && item.Kind == MediaKind.Episode;
        var watching = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
            .Select(p => new PlaybackPreferencesDto(p.AutoPlayNext, p.SkipIntros, p.SkipRecaps, p.SkipCredits, p.SubtitleSize, p.SubtitlePosition, p.SubtitlesInBlackBars)).FirstOrDefaultAsync(ct);
        var next = shuffling ? await ShuffleNextAsync(item, ct) : await NextEpisodeAsync(item, ct);
        // On shuffle the player keeps its own trail of what it played; in order, the episode before this one.
        var before = shuffling ? null : await PreviousEpisodeAsync(item, ct);
        var basePath = $"/api/v1/playback/{id}";
        var burned = decision.Transcode?.BurnSubtitle is not null;
        var series = ancestors.FirstOrDefault(a => a.Kind == MediaKind.Series)?.Id;
        var speed = series is { } seriesId
            ? await db.ShowPlaybackSpeeds.AsNoTracking().Where(s => s.ProfileId == profile.Id && s.SeriesId == seriesId).Select(s => (double?)s.Speed).FirstOrDefaultAsync(ct)
            : null;

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
                s.IsText ? $"{basePath}/subtitles/{s.Key}.vtt" : null, s.ExternalPath is not null, s.IsStyled ? $"{basePath}/subtitles/{s.Key}.ass" : null)).ToList(),
            decision.Subtitle?.Key,
            burned,
            file.Markers.OrderBy(m => m.StartSeconds).Select(m => new MarkerDto(m.Type, m.StartSeconds, m.EndSeconds)).ToList(),
            summary,
            ancestors,
            next is null ? null : await db.MediaItems.Where(i => i.Id == next.Id).Summaries(profile.Id).FirstAsync(ct),
            decision.Transcode is { } t ? Summary(t, file) : null,
            shuffling,
            watching,
            state?.SubtitleDelaySeconds ?? 0,
            before is null ? null : await db.MediaItems.Where(i => i.Id == before.Id).Summaries(profile.Id).FirstAsync(ct),
            file.AudioDelaySeconds,
            decision.Transcode?.AudioDelaySeconds is not (null or 0),
            speed,
            // Bazarr finds subtitles for what Sonarr and Radarr manage; not for a kids profile.
            options.CurrentValue.Bazarr?.Instance is not null && !db.ServesKids && item.Kind is (MediaKind.Movie or MediaKind.Episode) && item.ArrId is not null,
            subtitles.Any(s => s.IsStyled && s.ExternalPath is null) ? $"{basePath}/fonts.json" : null,
            decision.Transcode is { Sound: not null });
    }

    /// <summary>
    /// A podcast episode, played from its feed's host through the server (<see cref="PlaybackSession.RemoteUrl"/>):
    /// as it is, since podcasts are MP3 or AAC, which every player takes.
    /// </summary>
    private async Task<PlaybackInfoDto?> StartPodcastAsync(MediaItem item, PlaybackRequest request, CancellationToken ct)
    {
        var enclosure = await db.PodcastEpisodes.AsNoTracking().FirstOrDefaultAsync(e => e.MediaItemId == item.Id, ct);
        if (enclosure is null)
        {
            return null;
        }

        if (request.ReplacesSessionId is { } replaces && sessions.Get(replaces) is { } previous && previous.ProfileId == profile.Id)
        {
            await sessions.RemoveAsync(previous.Id);
        }

        // Playing now, so no longer up next (on any of this profile's devices).
        await db.PodcastQueue.Where(q => q.ProfileId == profile.Id && q.EpisodeId == item.Id).ExecuteDeleteAsync(ct);

        var state = await db.PlaybackStates.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == profile.Id && p.MediaItemId == item.Id, ct);
        var duration = enclosure.DurationSeconds ?? state?.DurationSeconds ?? (item.RuntimeMinutes ?? 0) * 60;
        var id = TranscodeManager.NewId();
        sessions.Add(new PlaybackSession
        {
            Id = id,
            ItemId = item.Id,
            ProfileId = profile.Id,
            SignInId = profile.SignInId,
            FileId = Guid.Empty,
            FilePath = "",
            RemoteUrl = enclosure.AudioUrl,
            Container = enclosure.MimeType,
            DurationSeconds = duration,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = null,
        });

        // A podcast resumes from wherever it was left, however near the start: they're long, and listened to in bits.
        var start = request.StartPositionSeconds
            ?? (state is { Watched: false, PositionSeconds: > 0 } && (duration <= 0 || state.PositionSeconds < duration * WatchedThreshold) ? state.PositionSeconds : 0);
        var summary = await db.MediaItems.Where(i => i.Id == item.Id).Summaries(profile.Id).FirstAsync(ct);
        var ancestors = item.ParentId is { } podcastId ? await db.MediaItems.Where(i => i.Id == podcastId).Summaries(profile.Id).ToListAsync(ct) : [];
        var watching = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
            .Select(p => new PlaybackPreferencesDto(p.AutoPlayNext, p.SkipIntros, p.SkipRecaps, p.SkipCredits, p.SubtitleSize, p.SubtitlePosition, p.SubtitlesInBlackBars)).FirstOrDefaultAsync(ct);
        return new PlaybackInfoDto(id, PlaybackMethod.DirectPlay, $"/api/v1/playback/{id}/stream", [], duration, Math.Max(0, start), Guid.Empty,
            [], null, [], null, false, [], summary, ancestors, null, null, false, watching);
    }

    /// <summary>
    /// A book: its files played back to back on one timeline, each as it is or converted for a player that can't decode
    /// it. The player gets the files' addresses with the session's id in them, so AirPlay and Chromecast can fetch them.
    /// </summary>
    private async Task<PlaybackInfoDto?> StartBookAsync(MediaItem book, PlaybackRequest request, CancellationToken ct)
    {
        var parts = await Books.BookPlayback.PartsAsync(db, book.Id, request.Profile.AudioCodecs, ct);
        if (parts.Count == 0)
        {
            return null;
        }

        if (request.ReplacesSessionId is { } replaces && sessions.Get(replaces) is { } previous && previous.ProfileId == profile.Id)
        {
            await sessions.RemoveAsync(previous.Id);
        }

        var id = TranscodeManager.NewId();
        var duration = parts.Sum(p => p.DurationSeconds);
        sessions.Add(new PlaybackSession
        {
            Id = id,
            ItemId = book.Id,
            ProfileId = profile.Id,
            SignInId = profile.SignInId,
            FileId = parts[0].FileId,
            FilePath = parts[0].Path,
            Container = parts[0].Container,
            DurationSeconds = duration,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = null,
            BookParts = parts,
            PlaysBooks = request.DeviceName is not null,
            // The name the device was given when it signed in ("Dan's iPhone") says which one better than its model.
            DeviceName = (profile.SignInId is { } signIn ? signIns?.List().FirstOrDefault(s => s.Id == signIn)?.Name : null) ?? request.DeviceName,
        });

        var basePath = $"/api/v1/playback/{id}";
        var manifest = await Books.BookPlayback.ManifestAsync(db, book, profile.Id, parts, p => $"{basePath}/book/{p.Index}", ct);
        var start = request.StartPositionSeconds ?? (manifest.Progress.Finished ? 0 : manifest.Progress.PositionSeconds);
        bookConverter?.Prepare(parts, start); // the files this player can't decode, converted while it gets going
        var summary = await db.MediaItems.Where(i => i.Id == book.Id).Summaries(profile.Id).FirstAsync(ct);
        return new PlaybackInfoDto(id, PlaybackMethod.DirectPlay, manifest.Files[0].Url, [], duration, Math.Clamp(start, 0, duration), parts[0].FileId,
            // No next for an older app: it would go on to the next book when the first file of this one ends.
            [], null, [], null, false, [], summary, [], request.DeviceName is null ? null : manifest.NextInSeries, null, Speed: manifest.Speed, Book: manifest);
    }

    public async Task ReportProgressAsync(PlaybackSession session, ProgressRequest progress, CancellationToken ct)
    {
        if (session.BookParts is not null)
        {
            // A book's place is on its whole timeline, and kept by its own rules (see BookPlayback). An older app plays
            // a book as a video, its first file only: its places are in that file, not the book, and would overwrite
            // the real one, so a book of several files isn't recorded from it at all.
            if (!session.PlaysBooks && session.BookParts.Count > 1)
            {
                return;
            }

            var ended = progress.Ended;
            await Books.BookPlayback.RecordAsync(db, session.ItemId, session.ProfileId,
                new Books.BookProgressRequest(progress.PositionSeconds, session.DeviceName, DateTime.UtcNow, ended), ct);
            return;
        }

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
            // A film finished again isn't another play, but a track heard through is, as it would be streaming.
            if (!state.Watched || item.Kind is MediaKind.Track or MediaKind.PodcastEpisode)
            {
                state.PlayCount++;
            }

            state.Watched = true;
            state.PositionSeconds = 0;
            if (item.Kind is MediaKind.Movie or MediaKind.Episode)
            {
                // Watched where Trakt couldn't hear it: it goes to Trakt's history with when it was.
                await Trakt.TraktOutbox.QueueHistoryAsync(db, profile.Id, [(itemId, playedAt)], ct);
            }
        }
        else if (progress.PositionSeconds > MinResumeSeconds)
        {
            state.PositionSeconds = progress.PositionSeconds;
            state.Watched = false;
        }

        await db.SaveChangesAsync(ct);
        Watchlist.WatchlistService.LookAgain();
        return true;
    }

    /// <summary>Applies a progress report to the state; true when it counted a new play.</summary>
    private static bool ApplyProgress(PlaybackState state, PlaybackSession session, ProgressRequest progress)
    {

        var duration = session.DurationSeconds > 0 ? session.DurationSeconds : (double?)null;
        // A podcast's last minutes are often still the conversation, so it counts as played only at the very end.
        var threshold = session.RemoteUrl is null ? WatchedThreshold : 0.98;
        var finished = progress.Ended
            || (duration is { } d && progress.PositionSeconds >= d * threshold)
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
        else if (progress.PositionSeconds > MinResumeSeconds || (session.RemoteUrl is not null && progress.PositionSeconds > 0))
        {
            state.PositionSeconds = progress.PositionSeconds;
            state.Watched = false;
        }

        return false;
    }

    /// <summary>
    /// Marks an item (or every episode of a series or season) watched or unwatched. Unwatched also forgets the place
    /// reached in something part way through, which takes it out of Continue Watching; for an episode, its show's next
    /// episode stays out of the row too, until another episode is played.
    /// </summary>
    public async Task<bool> SetWatchedAsync(Guid itemId, bool watched, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return false;
        }

        if (item.Kind == MediaKind.Audiobook)
        {
            // A book keeps when it was finished, and forgets it when it's marked not finished.
            return await Books.BookPlayback.SetFinishedAsync(db, itemId, profile.Id, watched, ct);
        }

        var targets = item.Kind switch
        {
            MediaKind.Series => await db.MediaItems.Where(e => e.Parent!.ParentId == itemId).Select(e => e.Id).ToListAsync(ct),
            MediaKind.Season or MediaKind.Podcast => await db.MediaItems.Where(e => e.ParentId == itemId).Select(e => e.Id).ToListAsync(ct),
            _ => [itemId],
        };

        var existing = await db.PlaybackStates.Where(p => p.UserId == profile.Id && targets.Contains(p.MediaItemId))
            .ToDictionaryAsync(p => p.MediaItemId, ct);
        var cleared = false;
        var nowWatched = new List<(Guid, DateTime)>();
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
            else if (!watched && !state.Watched)
            {
                // Part way through: forget the place but keep when it was played, so history stays true.
                cleared |= state.PositionSeconds > 0;
                state.PositionSeconds = 0;
                continue;
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

            if (watched)
            {
                nowWatched.Add((id, state.LastPlayedAt));
            }
        }

        // Marked watched here: Trakt hears of it. Marked unwatched isn't passed on; Trakt's history stays as it was.
        await Trakt.TraktOutbox.QueueHistoryAsync(db, profile.Id, nowWatched, ct);

        if (cleared && item.Kind == MediaKind.Episode)
        {
            await DismissShowAsync(item, ct);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Takes a title out of Continue Watching without marking it watched: forgets the place reached in it and, for an
    /// episode (or a show), keeps the show's next episode out of the row until another episode is played.
    /// </summary>
    public async Task<bool> RemoveFromContinueWatchingAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return false;
        }

        await db.PlaybackStates.Where(p => p.UserId == profile.Id && p.MediaItemId == itemId && !p.Watched)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.PositionSeconds, 0), ct);
        await DismissShowAsync(item, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Notes that this profile took the show of <paramref name="item"/> (an episode or the show) out of Continue Watching.</summary>
    private async Task DismissShowAsync(MediaItem item, CancellationToken ct)
    {
        var seriesId = item.Kind switch
        {
            MediaKind.Series => item.Id,
            MediaKind.Episode when item.ParentId is { } season => await db.MediaItems.Where(s => s.Id == season).Select(s => s.ParentId).FirstOrDefaultAsync(ct),
            _ => null,
        };
        if (seriesId is not { } id)
        {
            return;
        }

        var dismissal = await db.ContinueWatchingDismissals.FindAsync([profile.Id, id], ct);
        if (dismissal is null)
        {
            db.ContinueWatchingDismissals.Add(new ContinueWatchingDismissal { ProfileId = profile.Id, SeriesId = id });
        }
        else
        {
            dismissal.DismissedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// What "Play" on a series should start: an episode in progress, else the one after the last watched,
    /// else the first unwatched (specials last), else the very first.
    /// </summary>
    public Task<MediaItem?> NextUpAsync(Guid seriesId, CancellationToken ct) => NextUpAsync(seriesId, profile.Id, ct);

    /// <summary>As <see cref="NextUpAsync(Guid, CancellationToken)"/>, for another profile: what a device would play, when telling it to.</summary>
    public async Task<MediaItem?> NextUpAsync(Guid seriesId, string profileId, CancellationToken ct)
    {
        var episodes = db.MediaItems.AsNoTracking().Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == seriesId);
        var mine = db.PlaybackStates.AsNoTracking().Where(p => p.UserId == profileId && p.MediaItem!.Parent!.ParentId == seriesId);

        var inProgress = await mine.Where(p => !p.Watched && p.PositionSeconds > 0).OrderByDescending(p => p.LastPlayedAt)
            .Select(p => p.MediaItem).FirstOrDefaultAsync(ct);
        if (inProgress is not null)
        {
            return inProgress;
        }

        if (UpNext(await EpisodesAsync(db, profileId, [seriesId], ct)) is { } next)
        {
            return await db.MediaItems.AsNoTracking().FirstAsync(e => e.Id == next.Id, ct);
        }

        var ordered = episodes.OrderBy(e => e.SeasonNumber == 0).ThenBy(e => e.SeasonNumber).ThenBy(e => e.SortTitle);
        return await ordered.FirstOrDefaultAsync(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched), ct)
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

    /// <summary>The episode before this one: previous in the season, else the last of the previous season.</summary>
    public async Task<MediaItem?> PreviousEpisodeAsync(MediaItem episode, CancellationToken ct)
    {
        if (episode.Kind != MediaKind.Episode || episode.ParentId is not { } seasonId
            || await db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.ParentId).FirstOrDefaultAsync(ct) is not { } seriesId)
        {
            return null;
        }

        var episodes = await EpisodesAsync(db, profile.Id, [seriesId], ct);
        return episodes.FirstOrDefault(e => e.Id == episode.Id) is { } current && PreviousBefore(episodes, current) is { } previous
            ? await db.MediaItems.AsNoTracking().FirstAsync(e => e.Id == previous.Id, ct)
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


    /// <summary>
    /// The episode before <paramref name="current"/>: the mirror of <see cref="NextAfter"/>. Previous in its season, else
    /// the last of the previous regular season.
    /// </summary>
    internal static EpisodeRef? PreviousBefore(IReadOnlyList<EpisodeRef> episodes, EpisodeRef current)
    {
        var season = episodes.Where(e => e.SeasonId == current.SeasonId);
        EpisodeRef? previous;
        if (current.AirDate is not null)
        {
            var byDate = season.Where(e => e.AirDate is not null)
                .OrderBy(e => e.AirDate).ThenBy(e => e.SortTitle, StringComparer.Ordinal).ThenBy(e => e.Id).ToList();
            previous = byDate.TakeWhile(e => e.Id != current.Id).LastOrDefault();
        }
        else
        {
            var before = current.EpisodeNumber ?? 0;
            previous = season.Where(e => (e.EpisodeNumberEnd ?? e.EpisodeNumber) < before)
                .OrderByDescending(e => e.EpisodeNumberEnd ?? e.EpisodeNumber).ThenByDescending(e => e.SortTitle, StringComparer.Ordinal).FirstOrDefault();
        }

        return previous ?? episodes.Where(e => e.SeasonNumber < current.SeasonNumber && e.SeasonNumber > 0)
            .OrderByDescending(e => e.SeasonNumber).ThenByDescending(e => e.SortTitle, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>What the player's info panel says about the stream: "Original HEVC (Dolby Vision), FLAC 7.1 sound".</summary>
    internal static string Summary(TranscodePlan plan, MediaFile file)
    {
        var sound = plan.CopyAudio ? null : plan.LosslessAudio ? $"lossless FLAC {Channels(plan.AudioChannels)} sound" : null;
        if (plan.Repackage is { } repackage)
        {
            // HDR10+ travels inside the copied video, so it's there whenever the HDR10 is.
            var range = file.VideoRange switch
            {
                VideoRange.DolbyVision when !repackage.StripDolbyVision => file.Hdr10Plus == true ? " (Dolby Vision, HDR10+)" : " (Dolby Vision)",
                VideoRange.Sdr => "",
                _ when repackage.VideoRange == "HLG" => " (HLG)",
                _ => file.Hdr10Plus == true ? " (HDR10+)" : " (HDR10)",
            };
            var original = $"Original {(file.VideoCodec == "hevc" ? "HEVC" : "H.264")}{range}, repackaged";
            return sound is null ? original : $"{original}, {sound}";
        }

        var converted = $"{plan.TargetHeight}p H.264 at {plan.VideoBitrate / 1_000_000.0:0.#} Mbps" + (plan.ToneMap ? ", HDR converted to SDR" : "");
        return sound is null ? converted : $"{converted}, {sound}";
    }

    private static string Channels(int channels) => channels switch { 8 => "7.1", 6 => "5.1", 2 => "stereo", 1 => "mono", _ => $"{channels}-channel" };

    public static string MasterPlaylist(PlaybackSession session, bool includeSubtitles)
    {
        var plan = session.Transcode!.Plan;
        var builder = new StringBuilder(plan.Fmp4 ? "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n" : "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n");
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

            var language = PlaylistText(Languages.Tag(subtitle.Language)) is { Length: > 0 } tag ? tag : "und";
            builder.Append(CultureInfo.InvariantCulture,
                $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{name}\",LANGUAGE=\"{language}\",DEFAULT=NO,AUTOSELECT={(subtitle.IsForced ? "YES" : "NO")},FORCED={(subtitle.IsForced ? "YES" : "NO")},URI=\"subtitles/{subtitle.Key}.m3u8\"\n");
        }

        // Music has no video: saying H.264 can leave players that set up from CODECS waiting for pictures.
        var audioOnly = plan.VideoStreamIndex < 0;
        var video = audioOnly ? null : plan.Repackage?.Codecs ?? $"avc1.{(plan.TargetHeight > 1080 ? "640033" : "640028")}";
        var codecs = string.Join(',', new[] { video, plan.AudioCodecTag }.OfType<string>());
        var resolution = audioOnly ? "" : $",RESOLUTION={plan.TargetWidth}x{plan.TargetHeight}";
        var bandwidth = plan.VideoBitrate + plan.AudioBitrate;
        // Repackaged video says what it is, so the TV switches to HDR or Dolby Vision for it; and the Apple TV wants
        // the full account of an HDR stream before it will take it: the soundtrack declared as a rendition (muxed, so
        // no URI) with its channels, the frame rate, the average bandwidth and that there are no closed captions.
        var range = "";
        var audioGroup = "";
        if (plan.Repackage is { } repackage)
        {
            range = $",VIDEO-RANGE={repackage.VideoRange}{(repackage.SupplementalCodecs is { } dv ? $",SUPPLEMENTAL-CODECS=\"{dv}\"" : "")}";
            if (repackage.FrameRate is { } fps)
            {
                range += string.Create(CultureInfo.InvariantCulture, $",FRAME-RATE={fps:0.000}");
            }

            if (plan.AudioCodecTag is not null)
            {
                var language = PlaylistText(Languages.Tag(session.AudioLanguage)) is { Length: > 0 } tag ? tag : "und";
                var channels = session.AudioIsAtmos && plan.CopyAudio && plan.SourceAudioCodec == "eac3" ? "16/JOC" : plan.AudioChannels.ToString(CultureInfo.InvariantCulture);
                builder.Append(CultureInfo.InvariantCulture,
                    $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"{PlaylistText(Languages.Label(session.AudioLanguage, null, false))}\",LANGUAGE=\"{language}\",DEFAULT=YES,AUTOSELECT=YES,CHANNELS=\"{channels}\"\n");
                audioGroup = ",AUDIO=\"aud\"";
            }

            range = $",AVERAGE-BANDWIDTH={bandwidth}" + range + audioGroup + ",CLOSED-CAPTIONS=NONE";
        }

        builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth}{resolution},CODECS=\"{codecs}\"{range}{(text.Count > 0 ? ",SUBTITLES=\"subs\"" : "")}\nmain.m3u8\n");
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
