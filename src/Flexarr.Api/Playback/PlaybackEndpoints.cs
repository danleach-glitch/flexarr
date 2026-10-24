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

        api.MapGet("/items/{id:guid}/next-up", async (Guid id, FlexarrDbContext db, PlaybackService playback, CancellationToken ct) =>
            await playback.NextUpAsync(id, ct) is { } episode
                ? Results.Ok(await db.MediaItems.Where(i => i.Id == episode.Id).Summaries().FirstAsync(ct))
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
        }).WithTags("Playback");

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

        group.MapGet("/subtitles/{key}.vtt", async (string sid, string key, bool? hls, PlaybackSessions sessions, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (sessions.Get(sid) is not { } session || session.Subtitles.FirstOrDefault(s => s.Key == key) is not { } track)
            {
                return Results.NotFound();
            }

            var file = await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Id == session.FileId, ct);
            var path = await Subtitles.ToWebVttAsync(file, track, TranscodeSettingsFor(options.CurrentValue), ct);
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

            session.LastPosition = progress.PositionSeconds;
            session.Paused = progress.Paused;
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
