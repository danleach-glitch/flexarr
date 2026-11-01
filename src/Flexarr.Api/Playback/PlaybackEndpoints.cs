using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Events;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Playback;

public static partial class PlaybackEndpoints
{
    /// <summary>How far a file's sound can be moved either way: as far as AetherEngine moves it.</summary>
    public const double MaxAudioDelaySeconds = 2;

    /// <summary>Starting playback, watch state and Continue Watching (authenticated).</summary>
    public static RouteGroupBuilder MapPlaybackEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/playback", async (PlaybackRequest request, PlaybackService playback, ILogger<PlaybackService> logger, CancellationToken ct) =>
        {
            // JSON leaves it null when the body has none, whatever the record says.
            if (request.Profile is null)
            {
                return Results.Problem("Say what the player can play: the request needs a profile.", statusCode: 400);
            }

            try
            {
                if (await playback.StartAsync(request, ct) is not { } info)
                {
                    return Results.NotFound();
                }

                // The first line of every playback's story: search the Logs page for its id to follow it.
                LogStarted(logger, info.PlaybackId, info.Item.Title, info.Method, info.Reasons.Count > 0 ? $" ({string.Join("; ", info.Reasons)})" : "");
                return Results.Ok(info);
            }
            catch (TranscodeLimitException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "The server is busy");
            }
            catch (PlaybackTakenOverException ex)
            {
                return TakenOver(ex.Message);
            }
        }).WithTags("Playback");

        // Each profile's own: anyone signed in as it may change them, admin or not.
        api.MapGet("/me/playback-preferences", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
                .Select(p => new PlaybackPreferencesDto(p.AutoPlayNext, p.SkipIntros, p.SkipRecaps, p.SkipCredits, p.SubtitleSize, p.SubtitlePosition, p.SubtitlesInBlackBars)).FirstOrDefaultAsync(ct) is { } found
                ? Results.Ok(found)
                : Results.NotFound()).WithTags("Playback");

        api.MapPut("/me/playback-preferences", async (PlaybackPreferencesDto preferences, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([profile.Id], ct) is not { } found)
            {
                return Results.NotFound();
            }

            found.AutoPlayNext = preferences.AutoPlayNext;
            found.SkipIntros = preferences.SkipIntros;
            found.SkipRecaps = preferences.SkipRecaps;
            found.SkipCredits = preferences.SkipCredits;
            found.SubtitleSize = preferences.SubtitleSize ?? found.SubtitleSize;
            found.SubtitlePosition = preferences.SubtitlePosition ?? found.SubtitlePosition;
            found.SubtitlesInBlackBars = preferences.SubtitlesInBlackBars ?? found.SubtitlesInBlackBars;
            await db.SaveChangesAsync(ct);
            return Results.Ok(preferences with
            {
                SubtitleSize = found.SubtitleSize, SubtitlePosition = found.SubtitlePosition, SubtitlesInBlackBars = found.SubtitlesInBlackBars,
            });
        }).WithTags("Playback");

        // Remembered for the title, and applied to its subtitles from the next time they're fetched.
        api.MapPut("/items/{id:guid}/subtitle-delay", async (Guid id, SubtitleDelayRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!double.IsFinite(request.Seconds) || Math.Abs(request.Seconds) > 60)
            {
                return Results.Problem("Subtitles can be moved up to a minute either way.", statusCode: 400);
            }

            if (!await db.MediaItems.AnyAsync(i => i.Id == id, ct))
            {
                return Results.NotFound();
            }

            var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == profile.Id && p.MediaItemId == id, ct);
            if (state is null)
            {
                state = new PlaybackState { UserId = profile.Id, MediaItemId = id };
                db.PlaybackStates.Add(state);
            }

            state.SubtitleDelaySeconds = Math.Round(request.Seconds, 3);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Playback");

        // A fault of the file, so fixed for the household: whoever nudges it fixes it for everyone.
        api.MapPut("/items/{id:guid}/audio-delay", async (Guid id, AudioDelayRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (!double.IsFinite(request.Seconds) || Math.Abs(request.Seconds) > MaxAudioDelaySeconds)
            {
                return Results.Problem("The sound can be moved up to two seconds either way.", statusCode: 400);
            }

            if (!await db.MediaItems.AnyAsync(i => i.Id == id, ct))
            {
                return Results.NotFound();
            }

            var seconds = Math.Round(request.Seconds, 3);
            await db.MediaFiles.Where(f => f.MediaItemId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.AudioDelaySeconds, seconds), ct);
            return Results.NoContent();
        }).WithTags("Playback");

        // Each profile's own, kept for the show an episode belongs to.
        api.MapPut("/items/{id:guid}/playback-speed", async (Guid id, PlaybackSpeedRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!double.IsFinite(request.Speed) || request.Speed is < 0.25 or > 4)
            {
                return Results.Problem("Speeds go from a quarter to four times.", statusCode: 400);
            }

            if (await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) is not { } item)
            {
                return Results.NotFound();
            }

            // A book keeps its own speed, even 1× when the profile's default for books is something else.
            var bookDefault = item.Kind == MediaKind.Audiobook
                ? await db.Profiles.Where(p => p.Id == profile.Id).Select(p => (double?)p.AudiobookSpeed).FirstOrDefaultAsync(ct) ?? 1
                : 1;
            var series = item.Kind switch
            {
                MediaKind.Audiobook => item.Id,
                MediaKind.Series => item.Id,
                MediaKind.Season => item.ParentId,
                MediaKind.Episode => await db.MediaItems.Where(s => s.Id == item.ParentId).Select(s => s.ParentId).FirstOrDefaultAsync(ct),
                _ => null,
            };
            if (series is not { } seriesId)
            {
                return Results.NoContent(); // Only shows keep a speed; a film's is for that time.
            }

            var kept = await db.ShowPlaybackSpeeds.FirstOrDefaultAsync(s => s.ProfileId == profile.Id && s.SeriesId == seriesId, ct);
            if (Math.Abs(request.Speed - bookDefault) < 0.001)
            {
                if (kept is not null)
                {
                    db.ShowPlaybackSpeeds.Remove(kept);
                }
            }
            else if (kept is null)
            {
                db.ShowPlaybackSpeeds.Add(new ShowPlaybackSpeed { ProfileId = profile.Id, SeriesId = seriesId, Speed = request.Speed });
            }
            else
            {
                kept.Speed = request.Speed;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Playback");

        api.MapGet("/items/{id:guid}/next-up", async (Guid id, FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, CancellationToken ct) =>
            await playback.NextUpAsync(id, ct) is { } episode
                ? Results.Ok(await db.MediaItems.Where(i => i.Id == episode.Id).Summaries(profile.Id).FirstAsync(ct))
                : Results.NoContent())
            .WithTags("Playback");

        api.MapPost("/items/{id:guid}/analyze", async (Guid id, FlexarrDbContext db, MarkerQueue queue, CancellationToken ct) =>
        {
            // A series analyses all its seasons; a season or episode analyses that season.
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var seasons = item.Kind switch
            {
                MediaKind.Series => await db.MediaItems.Where(s => s.ParentId == id).Select(s => s.Id).ToListAsync(ct),
                MediaKind.Season => [id],
                MediaKind.Episode when item.ParentId is { } season => [season],
                _ => [],
            };
            await db.MediaFiles.Where(f => f.MediaItem!.ParentId != null && seasons.Contains(f.MediaItem.ParentId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.MarkersScannedAt, (DateTime?)null), ct);
            foreach (var season in seasons)
            {
                queue.Prioritize(season);
            }

            return Results.Accepted();
        }).WithTags("Playback").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        api.MapPost("/items/{id:guid}/watched", async (Guid id, WatchedRequest request, PlaybackService playback, CancellationToken ct) =>
            await playback.SetWatchedAsync(id, request.Watched, ct) ? Results.NoContent() : Results.NotFound())
            .WithTags("Playback");

        return api;
    }

    /// <summary>
    /// Media delivery. Anonymous because native players (Safari, AVPlayer) can't add headers;
    /// the unguessable session id is the credential.
    /// </summary>
    public static RouteGroupBuilder MapStreamEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/playback/{sid}").WithTags("Streaming").AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                // A Chromecast plays a converted stream with a web page's player, which may read its playlists and
                // segments only when they say any page can. Reads only, and nothing a page couldn't already fetch
                // with the address: the session id in it is the credential, not a cookie or key.
                var http = context.HttpContext;
                if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                {
                    http.Response.Headers.AccessControlAllowOrigin = "*";
                }

                // Whatever's logged while serving the stream says which playback it was.
                using var _ = Serilog.Context.LogContext.PushProperty("PlaybackId",
                    Logging.Redactor.ShortHash(http.Request.RouteValues["sid"] as string ?? ""));
                return await next(context);
            });

        // The viewer chose subtitles (or none) in the player: the session takes note, so the choice is what the next
        // sitting and the next episode start with. Apple's player switches renditions on the device, so without this
        // the server's own first choice came back every time.
        group.MapPut("/subtitle", async (string sid, SubtitleChoiceRequest request, PlaybackSessions sessions, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

            string? language = null;
            if (request.Key != "off")
            {
                if (session.Subtitles.FirstOrDefault(s => s.Key == request.Key) is not { } track)
                {
                    return Results.NotFound();
                }

                language = track.IsForced ? null : track.Language; // forced tracks come on by themselves; not a preference
            }

            session.SubtitleLanguage = language;
            var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == session.ProfileId && p.MediaItemId == session.ItemId, ct);
            if (state is null)
            {
                state = new PlaybackState { UserId = session.ProfileId, MediaItemId = session.ItemId };
                db.PlaybackStates.Add(state);
            }

            state.SubtitleLanguage = language;
            state.LastPlayedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Playback");

        // A book's file n, as it is or converted (see BookPlayback.PartsAsync).
        group.MapGet("/book/{index:int}", async (string sid, int index, PlaybackSessions sessions, Books.BookFileConverter converter, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { BookParts: { } parts } session || index < 0 || index >= parts.Count)
            {
                return Results.NotFound();
            }

            session.LastSeen = DateTime.UtcNow;
            return await Books.BookEndpoints.ServeAsync(parts[index], parts[index].Convert, converter, ct);
        });

        group.MapGet("/stream", async (string sid, PlaybackSessions sessions, Podcasts.PodcastFetcher podcasts, HttpContext http, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { Method: PlaybackMethod.DirectPlay } session)
            {
                return Results.NotFound();
            }

            if (session.RemoteUrl is { } remote)
            {
                session.LastSeen = DateTime.UtcNow;
                session.ResolvedRemoteUrl = await Podcasts.PodcastRelay.RelayAsync(podcasts, remote, session.ResolvedRemoteUrl, session.Container, http, ct);
                return Results.Empty;
            }

            return Results.File(session.FilePath, ContentType(session.Container), enableRangeProcessing: true);
        });

        group.MapGet("/master.m3u8", (string sid, bool? subtitles, PlaybackSessions sessions, TranscodeManager transcodes, HttpContext http) =>
            sessions.Get(sid) is { Transcode: not null } session
                ? Playlist(http, PlaybackService.MasterPlaylist(session, subtitles != false))
                : Missing(transcodes, sid));

        group.MapGet("/main.m3u8", (string sid, PlaybackSessions sessions, TranscodeManager transcodes, HttpContext http) =>
            sessions.Get(sid) is { Transcode: { } transcode }
                ? Playlist(http, transcode.MediaPlaylist())
                : Missing(transcodes, sid));

        // Fragmented MP4 (Apple's players): the initialisation segment, then .m4s segments served like .ts ones.
        group.MapGet("/init.mp4", async (string sid, PlaybackSessions sessions, TranscodeManager transcodes, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { Transcode: { } transcode })
            {
                return Missing(transcodes, sid);
            }

            try
            {
                return await transcode.GetInitBytesAsync(ct) is { } init ? Results.Bytes(init, "video/mp4") : Results.NotFound();
            }
            catch (TranscodeLimitException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "The server is busy");
            }
        });
        group.MapGet("/{segment:int}.m4s", (string sid, int segment, PlaybackSessions sessions, TranscodeManager transcodes, CancellationToken ct) =>
            Segment(sid, segment, "video/iso.segment", sessions, transcodes, ct));
        group.MapGet("/{segment:int}.ts", (string sid, int segment, PlaybackSessions sessions, TranscodeManager transcodes, CancellationToken ct) =>
            Segment(sid, segment, "video/mp2t", sessions, transcodes, ct));

        // page: teletext's page to read (100–899), for a broadcast whose subtitles aren't on the page it marks as subtitles.
        group.MapGet("/subtitles/{key}.vtt", async (string sid, string key, bool? hls, int? page, PlaybackSessions sessions, FlexarrDbContext db, SubtitleCache subtitles,
            CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session || session.Subtitles.FirstOrDefault(s => s.Key == key) is not { } track)
            {
                return Results.NotFound();
            }

            if (page is { } number && (!track.IsTeletext || !Subtitles.IsTeletextPage(number)))
            {
                return Results.Problem("Only teletext subtitles have a page, numbered 100 to 899.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!track.IsText)
            {
                return Results.Problem("Picture-based subtitles can only be burned into the video.", statusCode: 422);
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            var path = page is { } chosen
                ? await subtitles.GetTeletextPageAsync(file, track, chosen, ct)
                : await subtitles.GetAsync(file, session.Subtitles, track, ct);
            if (path is null)
            {
                return Results.Problem("These subtitles couldn't be converted.", statusCode: 422);
            }

            var delay = await db.PlaybackStates.AsNoTracking().Where(p => p.UserId == session.ProfileId && p.MediaItemId == session.ItemId)
                .Select(p => p.SubtitleDelaySeconds).FirstOrDefaultAsync(ct);
            if (hls != true && delay == 0)
            {
                return Results.File(path, "text/vtt");
            }

            var vtt = Subtitles.Shift(await File.ReadAllTextAsync(path, ct), delay);
            if (hls != true)
            {
                return Results.Text(vtt, "text/vtt");
            }

            // Apple's own player draws these as the cues say, so the profile's position goes into the cues.
            var position = await db.Profiles.AsNoTracking().Where(p => p.Id == session.ProfileId).Select(p => p.SubtitlePosition).FirstOrDefaultAsync(ct);
            return Results.Text(Subtitles.WithHlsTimestampMap(Subtitles.Placed(vtt, position), session.Transcode?.Plan.Fmp4 == true), "text/vtt");
        });

        // Styled subtitles as authored, for players that draw them with their own fonts, colours and positions.
        group.MapGet("/subtitles/{key}.ass", async (string sid, string key, PlaybackSessions sessions, FlexarrDbContext db, SubtitleCache subtitles,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session || session.Subtitles.FirstOrDefault(s => s.Key == key) is not { IsStyled: true } track)
            {
                return Results.NotFound();
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            var settings = TranscodeSettingsFor(options.CurrentValue);
            var text = await Subtitles.StyledAsync(file, track, settings, ct);
            if (text is null)
            {
                // Converted before styled copies were kept, or not yet: converting again keeps one.
                await subtitles.GetAsync(file, session.Subtitles, track, ct, styled: true);
                text = await Subtitles.StyledAsync(file, track, settings, ct);
            }

            return text is null ? Results.Problem("These subtitles couldn't be read.", statusCode: 422) : Results.Text(text, "text/x-ssa");
        });

        // The fonts attached to the file, which its styled subtitles are drawn with.
        group.MapGet("/fonts.json", async (string sid, PlaybackSessions sessions, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            var fonts = await SubtitleFonts.ListAsync(file, options.CurrentValue.FfprobePath, ct);
            return Results.Ok(fonts.Select(f => new SubtitleFontDto(f.Name, $"/api/v1/playback/{sid}/fonts/{f.StreamIndex}")).ToList());
        });

        group.MapGet("/fonts/{index:int}", async (string sid, int index, PlaybackSessions sessions, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options,
            CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            if ((await SubtitleFonts.ListAsync(file, options.CurrentValue.FfprobePath, ct)).FirstOrDefault(f => f.StreamIndex == index) is not { } font
                || await SubtitleFonts.ExtractAsync(file, font, TranscodeSettingsFor(options.CurrentValue), ct) is not { } path)
            {
                return Results.NotFound();
            }

            return Results.File(path, font.ContentType, font.Name);
        });

        group.MapGet("/subtitles/{key}.m3u8", (string sid, string key, PlaybackSessions sessions, HttpContext http) =>
            sessions.Get(sid) is { } session && session.Subtitles.Any(s => s.Key == key)
                ? Playlist(http, PlaybackService.SubtitlePlaylist(session, key))
                : Results.NotFound());

        // Scrubbing previews: 404 until they've been generated (the player asks again later).
        group.MapGet("/trickplay.json", (string sid, PlaybackSessions sessions, IOptionsMonitor<FlexarrOptions> options) =>
            sessions.Get(sid) is { } session && Trickplay.Read(options.CurrentValue.ResolvedCacheDirectory, session.FileId) is { } manifest
                ? Results.Ok(manifest)
                : Results.NotFound());

        group.MapGet("/trickplay/{sheet:int}.jpg", (string sid, int sheet, PlaybackSessions sessions, IOptionsMonitor<FlexarrOptions> options, HttpContext http) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

            var path = Trickplay.SheetPath(options.CurrentValue.ResolvedCacheDirectory, session.FileId, sheet);
            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=86400";
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });

        group.MapPost("/progress", async (string sid, ProgressRequest progress, PlaybackSessions sessions, PlaybackService playback, PlaybackEvents events,
            Books.BookFileConverter bookConverter, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

            if (session.BookParts is { } parts && parts.Any(p => p.Convert) && double.IsFinite(progress.PositionSeconds))
            {
                bookConverter.Prepare(parts, progress.PositionSeconds); // the next few files this player can't decode, ahead of it
            }

            events.Observe(session, progress);
            sessions.Reported(session, progress);
            await playback.ReportProgressAsync(session, progress, ct);
            return Results.NoContent();
        });

        group.MapDelete("", async (string sid, PlaybackSessions sessions) =>
        {
            await sessions.RemoveAsync(sid);
            return Results.NoContent();
        });

        return api;
    }

    public static TranscodeSettings TranscodeSettingsFor(FlexarrOptions options) => new()
    {
        FfmpegPath = options.FfmpegPath,
        CacheDirectory = options.ResolvedCacheDirectory,
        HardwareAcceleration = options.Transcoding.HardwareAcceleration,
        VaapiDevice = options.Transcoding.VaapiDevice,
        SoftwarePreset = options.Transcoding.SoftwarePreset,
        SoftwareFallback = options.Transcoding.SoftwareFallback,
        CanToneMapInSoftware = FfmpegFilters.Has(options.FfmpegPath, "zscale"),
        MaxConcurrentTranscodes = options.Transcoding.MaxConcurrentTranscodes,
    };

    /// <summary>A transcode segment (.ts or fragmented MP4 .m4s), converted if it hasn't been yet.</summary>
    private static async Task<IResult> Segment(string sid, int segment, string contentType, PlaybackSessions sessions, TranscodeManager transcodes, CancellationToken ct)
    {
        if (sessions.Get(sid) is not { Transcode: { } transcode })
        {
            return Missing(transcodes, sid);
        }

        string? path;
        try
        {
            path = await transcode.GetSegmentAsync(segment, ct);
        }
        catch (TranscodeLimitException ex)
        {
            // Back from a long pause to find the others using every slot. The player gives up and its recovery
            // asks for a new session, which is refused with this same message for the viewer to see.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "The server is busy");
        }

        return path is not null ? Results.File(path, contentType)
            : transcodes.TakenOverReason(sid) is { } reason ? TakenOver(reason) // ended while this request waited
            : Results.Problem("The segment couldn't be converted.", statusCode: 500);
    }

    /// <summary>A transcode that's gone: 410 with the reason when it was ended for another viewer, else 404.</summary>
    private static IResult Missing(TranscodeManager transcodes, string sid) =>
        transcodes.TakenOverReason(sid) is { } reason ? TakenOver(reason) : Results.NotFound();

    // Players show this rather than trying to get the stream back, which would only take it from the other screen.
    private static IResult TakenOver(string reason) =>
        Results.Problem(reason, statusCode: StatusCodes.Status410Gone, title: "Playing somewhere else");

    private static IResult Playlist(HttpContext http, string body)
    {
        http.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        return Results.Text(body, "application/vnd.apple.mpegurl");
    }

    private static string ContentType(string? container) => container switch
    {
        "mp4" => "video/mp4",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        "mkv" => "video/x-matroska",
        "avi" => "video/x-msvideo",
        "flac" => "audio/flac",
        "mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback {PlaybackId} of {Title}: {Method}{Reasons}")]
    private static partial void LogStarted(ILogger logger, string playbackId, string title, PlaybackMethod method, string reasons);
}
