using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Playback;

public static class PlaybackEndpoints
{
    /// <summary>Starting playback, watch state and Continue Watching (authenticated).</summary>
    public static RouteGroupBuilder MapPlaybackEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/playback", async (PlaybackRequest request, PlaybackService playback, CancellationToken ct) =>
            await playback.StartAsync(request, ct) is { } info ? Results.Ok(info) : Results.NotFound())
            .WithTags("Playback");

        api.MapPost("/items/{id:guid}/watched", async (Guid id, WatchedRequest request, PlaybackService playback, CancellationToken ct) =>
            await playback.SetWatchedAsync(id, request.Watched, ct) ? Results.NoContent() : Results.NotFound())
            .WithTags("Playback");

        api.MapGet("/home/continue", async (int? take, FlexarrDbContext db, PlaybackService playback, CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 20, 1, 50);
            var states = db.PlaybackStates.Where(p => p.UserId == PlaybackState.DefaultUser);

            // Partly watched movies and episodes.
            var inProgress = await states.Where(p => !p.Watched && p.PositionSeconds > 0)
                .OrderByDescending(p => p.LastPlayedAt).Take(limit)
                .Select(p => new { p.MediaItemId, p.LastPlayedAt, SeriesId = p.MediaItem!.Parent != null ? p.MediaItem.Parent.ParentId : null })
                .ToListAsync(ct);

            // Up next: the episode after the last one finished, for shows watched in the last three months.
            var since = DateTime.UtcNow.AddDays(-90);
            var lastFinished = await states.Where(p => p.Watched && p.LastPlayedAt > since && p.MediaItem!.Kind == MediaKind.Episode)
                .Select(p => new { p.MediaItemId, p.LastPlayedAt, SeriesId = p.MediaItem!.Parent!.ParentId })
                .ToListAsync(ct);
            var busySeries = inProgress.Select(p => p.SeriesId).Where(s => s is not null).ToHashSet();

            var upNext = new List<(Guid Id, DateTime At)>();
            foreach (var latest in lastFinished.GroupBy(p => p.SeriesId).Select(g => g.MaxBy(p => p.LastPlayedAt)!)
                         .Where(p => !busySeries.Contains(p.SeriesId)).OrderByDescending(p => p.LastPlayedAt).Take(limit))
            {
                var episode = await db.MediaItems.AsNoTracking().FirstAsync(i => i.Id == latest.MediaItemId, ct);
                var next = await playback.NextEpisodeAsync(episode, ct);
                if (next is not null && !await states.AnyAsync(p => p.MediaItemId == next.Id && p.Watched, ct))
                {
                    upNext.Add((next.Id, latest.LastPlayedAt));
                }
            }

            var order = inProgress.Select(p => (Id: p.MediaItemId, At: p.LastPlayedAt)).Concat(upNext)
                .OrderByDescending(x => x.At).Select(x => x.Id).Distinct().Take(limit).ToList();
            var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries().ToDictionaryAsync(i => i.Id, ct);
            return order.Where(items.ContainsKey).Select(id => items[id]).ToList();
        }).WithTags("Playback");

        return api;
    }

    /// <summary>
    /// Media delivery. Anonymous because native players (Safari, AVPlayer) can't add headers;
    /// the unguessable session id is the credential.
    /// </summary>
    public static RouteGroupBuilder MapStreamEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/playback/{sid}").WithTags("Streaming").AllowAnonymous();

        group.MapGet("/stream", (string sid, PlaybackSessions sessions) =>
            sessions.Get(sid) is { Method: PlaybackMethod.DirectPlay } session
                ? Results.File(session.FilePath, ContentType(session.Container), enableRangeProcessing: true)
                : Results.NotFound());

        group.MapGet("/master.m3u8", (string sid, bool? subtitles, PlaybackSessions sessions, HttpContext http) =>
            sessions.Get(sid) is { Transcode: not null } session
                ? Playlist(http, PlaybackService.MasterPlaylist(session, subtitles != false))
                : Results.NotFound());

        group.MapGet("/main.m3u8", (string sid, PlaybackSessions sessions, HttpContext http) =>
            sessions.Get(sid) is { Transcode: { } transcode }
                ? Playlist(http, transcode.MediaPlaylist())
                : Results.NotFound());

        group.MapGet("/{segment:int}.ts", async (string sid, int segment, PlaybackSessions sessions, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { Transcode: { } transcode })
            {
                return Results.NotFound();
            }

            var path = await transcode.GetSegmentAsync(segment, ct);
            return path is null ? Results.Problem("The segment couldn't be converted.", statusCode: 500) : Results.File(path, "video/mp2t");
        });

        group.MapGet("/subtitles/{key}.vtt", async (string sid, string key, bool? hls, PlaybackSessions sessions, FlexarrDbContext db, IOptions<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session || session.Subtitles.FirstOrDefault(s => s.Key == key) is not { } track)
            {
                return Results.NotFound();
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            var path = await Subtitles.ToWebVttAsync(file, track, TranscodeSettingsFor(options.Value), ct);
            if (path is null)
            {
                return Results.Problem("These subtitles couldn't be converted.", statusCode: 422);
            }

            return hls == true
                ? Results.Text(Subtitles.WithHlsTimestampMap(await File.ReadAllTextAsync(path, ct)), "text/vtt")
                : Results.File(path, "text/vtt");
        });

        group.MapGet("/subtitles/{key}.m3u8", (string sid, string key, PlaybackSessions sessions, HttpContext http) =>
            sessions.Get(sid) is { } session && session.Subtitles.Any(s => s.Key == key)
                ? Playlist(http, PlaybackService.SubtitlePlaylist(session, key))
                : Results.NotFound());

        group.MapPost("/progress", async (string sid, ProgressRequest progress, PlaybackSessions sessions, PlaybackService playback, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session)
            {
                return Results.NotFound();
            }

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
    };

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
        _ => "application/octet-stream",
    };
}
