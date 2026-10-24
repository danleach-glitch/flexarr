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

        var duration = file.DurationSeconds ?? (item.RuntimeMinutes ?? 0) * 60;
        var id = TranscodeManager.NewId();
        TranscodeSession? transcode = null;
        if (decision is { Method: PlaybackMethod.Transcode, Transcode: { } plan })
        {
            transcode = transcodes.Create(file.Path, duration, plan);
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

        var summary = await db.MediaItems.Where(i => i.Id == item.Id).Summaries(profile.Id).FirstAsync(ct);
        var ancestors = new List<ItemSummaryDto>();
        for (var parentId = item.ParentId; parentId is { } pid;)
        {
            ancestors.Insert(0, await db.MediaItems.Where(i => i.Id == pid).Summaries(profile.Id).FirstAsync(ct));
            parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstAsync(ct);
        }

        var next = await NextEpisodeAsync(item, ct);
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
            decision.Transcode is { } t ? $"{t.TargetHeight}p H.264 at {t.VideoBitrate / 1_000_000.0:0.#} Mbps" + (t.ToneMap ? ", HDR converted to SDR" : "") : null);
    }

    public async Task ReportProgressAsync(PlaybackSession session, ProgressRequest progress, CancellationToken ct)
    {
        var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == session.ProfileId && p.MediaItemId == session.ItemId, ct);
        if (state is null)
        {
            state = new PlaybackState { UserId = session.ProfileId, MediaItemId = session.ItemId };
            db.PlaybackStates.Add(state);
        }

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
                session.CountedAsPlay = true;
            }
        }
        else if (progress.PositionSeconds > MinResumeSeconds)
        {
            state.PositionSeconds = progress.PositionSeconds;
            state.Watched = false;
        }

        await db.SaveChangesAsync(ct);
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

        var last = await mine.Where(p => p.Watched).OrderByDescending(p => p.LastPlayedAt).Select(p => p.MediaItem).FirstOrDefaultAsync(ct);
        if (last is not null && await NextEpisodeAsync(last, ct) is { } next
            && !await mine.AnyAsync(p => p.MediaItemId == next.Id && p.Watched, ct))
        {
            return next;
        }

        var ordered = episodes.OrderBy(e => e.SeasonNumber == 0).ThenBy(e => e.SeasonNumber).ThenBy(e => e.SortTitle);
        return await ordered.FirstOrDefaultAsync(e => !e.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched), ct)
            ?? await ordered.FirstOrDefaultAsync(ct);
    }

    /// <summary>The episode after this one: next in the season, else the first of the next season.</summary>
    public async Task<MediaItem?> NextEpisodeAsync(MediaItem episode, CancellationToken ct)
    {
        if (episode.Kind != MediaKind.Episode || episode.ParentId is not { } seasonId)
        {
            return null;
        }

        var after = episode.EpisodeNumberEnd ?? episode.EpisodeNumber ?? 0;
        var next = await db.MediaItems.AsNoTracking()
            .Where(e => e.ParentId == seasonId && (episode.AirDate != null ? e.AirDate > episode.AirDate : e.EpisodeNumber > after))
            .OrderBy(e => e.SortTitle).FirstOrDefaultAsync(ct);
        if (next is not null)
        {
            return next;
        }

        var season = await db.MediaItems.AsNoTracking().FirstAsync(s => s.Id == seasonId, ct);
        var nextSeason = await db.MediaItems.AsNoTracking()
            .Where(s => s.ParentId == season.ParentId && s.SeasonNumber > season.SeasonNumber && s.SeasonNumber > 0)
            .OrderBy(s => s.SeasonNumber).FirstOrDefaultAsync(ct);
        return nextSeason is null ? null
            : await db.MediaItems.AsNoTracking().Where(e => e.ParentId == nextSeason.Id).OrderBy(e => e.SortTitle).FirstOrDefaultAsync(ct);
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
            var name = Languages.Label(subtitle.Language, subtitle.Title, subtitle.IsForced).Replace("\"", "'", StringComparison.Ordinal);
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{Languages.Label(subtitle.Language, subtitle.Title, subtitle.IsForced)} {n}";
            }

            builder.Append(CultureInfo.InvariantCulture,
                $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{name}\",LANGUAGE=\"{subtitle.Language ?? "und"}\",DEFAULT=NO,AUTOSELECT={(subtitle.IsForced ? "YES" : "NO")},FORCED={(subtitle.IsForced ? "YES" : "NO")},URI=\"subtitles/{subtitle.Key}.m3u8\"\n");
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
