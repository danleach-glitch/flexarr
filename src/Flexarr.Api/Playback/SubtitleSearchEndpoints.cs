using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>A subtitle a provider offers for a video, best match first.</summary>
/// <param name="Id">Passed back to download it.</param>
/// <param name="Release">The release it was made for, as the provider names it.</param>
/// <param name="Score">Bazarr's match score, out of 100.</param>
/// <param name="MatchesFile">Made for this exact file (matched by hash), so it should be in sync.</param>
public sealed record SubtitleResultDto(string Id, string Provider, string Language, string LanguageLabel, string Release, bool HearingImpaired,
    bool Forced, int Score, bool MatchesFile, string? Uploader);

public sealed record SubtitleSearchDto(IReadOnlyList<SubtitleResultDto> Results);

/// <param name="Result">A search result's id; omitted to let Flexarr pick the best match.</param>
/// <param name="FileId">Which copy of the title; omitted for the one playback would choose.</param>
public sealed record SubtitleDownloadRequest(string? Result = null, Guid? FileId = null);

/// <summary>The subtitle that was saved.</summary>
/// <param name="Key">Its track key in a new playback session of the same file.</param>
public sealed record SubtitleDownloadedDto(string Key, string Label);

/// <param name="Key">The track to re-sync: a subtitle file beside the video.</param>
public sealed record SubtitleSyncRequest(string Key, Guid? FileId = null);

/// <summary>A re-sync of one file's subtitle, which runs in the background.</summary>
/// <param name="State">running, done, failed, or none (nothing re-synced since the server started).</param>
/// <param name="Key">The track that was re-synced.</param>
public sealed record SubtitleSyncDto(string State, string? Key = null, string? Error = null);

/// <summary>Finding, downloading and re-syncing subtitles through Bazarr, for any profile, from the players and title pages.</summary>
public static partial class SubtitleSearchEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapSubtitleSearchEndpoints(this RouteGroupBuilder api)
    {
        // Whether Bazarr is set up, so clients can hide "Search for subtitles…" otherwise.
        api.MapGet("/subtitles/status", async (IOptionsMonitor<FlexarrOptions> options, BazarrClient bazarr, CancellationToken ct) =>
        {
            var instance = options.CurrentValue.Bazarr?.Instance;
            return new { configured = instance is not null, reachable = instance is not null && await SafePingAsync(bazarr, instance, ct) };
        }).WithTags("Subtitles");

        var group = api.MapGroup("/items/{id:guid}/subtitles").WithTags("Subtitles");

        group.MapGet("/search", async (Guid id, Guid? fileId, IOptionsMonitor<FlexarrOptions> options, BazarrClient bazarr, FlexarrDbContext db, CancellationToken ct) =>
        {
            var found = await FindAsync(id, fileId, options, bazarr, db, ct);
            if (found.Problem is { } problem)
            {
                return problem;
            }

            try
            {
                var results = await bazarr.SearchAsync(found.Bazarr!, found.Media!, ct);
                return Results.Ok(new SubtitleSearchDto(results.Select(ToDto).ToList()));
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Bazarr couldn't search: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/download", async (Guid id, SubtitleDownloadRequest request, IOptionsMonitor<FlexarrOptions> options, BazarrClient bazarr,
            FlexarrDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var found = await FindAsync(id, request.FileId, options, bazarr, db, ct);
            if (found.Problem is { } problem)
            {
                return problem;
            }

            BazarrSubtitleResult? chosen;
            if (request.Result is { } token)
            {
                chosen = Decode(token);
                if (chosen is null)
                {
                    return Results.Problem("That search result isn't one Flexarr knows. Search again.", statusCode: StatusCodes.Status400BadRequest);
                }
            }
            else
            {
                try
                {
                    chosen = Best(await bazarr.SearchAsync(found.Bazarr!, found.Media!, ct));
                }
                catch (HttpRequestException ex)
                {
                    return Results.Problem($"Bazarr couldn't search: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
                }

                if (chosen is null)
                {
                    return Results.Problem("No subtitles were found for this.", statusCode: StatusCodes.Status404NotFound);
                }
            }

            // Bazarr may replace a subtitle that's already there, so look for whatever changed since now.
            var since = DateTime.UtcNow.AddSeconds(-2);
            var file = found.File!;
            SubtitleTrack? saved;
            try
            {
                var before = await JobIdsAsync(bazarr, found.Bazarr!, ct);
                await bazarr.DownloadAsync(found.Bazarr!, found.Media!, chosen, ct);
                saved = await WaitForBazarrAsync(bazarr, found.Bazarr!, before, j => j.Name.StartsWith("Manually download", StringComparison.OrdinalIgnoreCase),
                    () => Subtitles.For(file)
                        .Where(t => t.ExternalPath is not null && File.GetLastWriteTimeUtc(t.ExternalPath) >= since)
                        .MaxBy(t => File.GetLastWriteTimeUtc(t.ExternalPath!)),
                    DownloadWait, ct);
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Bazarr couldn't download it: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }

            if (saved is null)
            {
                LogNotSaved(logs.CreateLogger(typeof(SubtitleSearchEndpoints)), file.Path);
                return Results.Problem("Bazarr didn't save the subtitles. The site may have refused the download (most allow only so many a day), or Bazarr " +
                    "saved them somewhere Flexarr doesn't look; Bazarr's log says which.", statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new SubtitleDownloadedDto(saved.Key, Languages.Label(saved.Language, saved.Title, saved.IsForced)));
        });

        group.MapPost("/sync", async (Guid id, SubtitleSyncRequest request, IOptionsMonitor<FlexarrOptions> options, BazarrClient bazarr,
            FlexarrDbContext db, SubtitleSyncs syncs, ILoggerFactory logs, CancellationToken ct) =>
        {
            var found = await FindAsync(id, request.FileId, options, bazarr, db, ct);
            if (found.Problem is { } problem)
            {
                return problem;
            }

            if (Subtitles.For(found.File!).FirstOrDefault(t => t.Key == request.Key) is not { ExternalPath: { } path } track)
            {
                return Results.Problem("Only subtitles saved beside the video can be re-synced.", statusCode: StatusCodes.Status400BadRequest);
            }

            // Bazarr sees the file beside its own path for the video; it may have indexed it with its language and flags.
            var name = Path.GetFileName(path);
            var media = found.Media!;
            var bazarrPath = media.Path[..(media.Path.LastIndexOfAny(['/', '\\']) + 1)] + name;
            var subtitle = media.Subtitles.FirstOrDefault(s => s.Path is { } p && FileName(p).Equals(name, StringComparison.Ordinal))
                ?? new BazarrSubtitleFile(Languages.Tag(track.Language), bazarrPath, track.IsForced, track.Title == "SDH");
            if (subtitle.Code2 is not { Length: 2 })
            {
                return Results.Problem("Bazarr can only re-sync subtitles whose language it knows.", statusCode: StatusCodes.Status400BadRequest);
            }

            var log = logs.CreateLogger(typeof(SubtitleSearchEndpoints));
            var instance = found.Bazarr!;
            var started = syncs.TryStart(found.File!.Id, track.Key, async stopping =>
            {
                try
                {
                    // Re-synced means the file was written again.
                    var written = File.GetLastWriteTimeUtc(path);
                    var before = await JobIdsAsync(bazarr, instance, stopping);
                    await bazarr.SyncAsync(instance, media, subtitle, bazarrPath, stopping);
                    var changed = await WaitForBazarrAsync(bazarr, instance, before, j => j.Name.Contains(name, StringComparison.Ordinal),
                        () => File.GetLastWriteTimeUtc(path) != written ? path : null, SyncWait, stopping);
                    if (changed is null)
                    {
                        LogSyncUnchanged(log, path);
                        return "Bazarr couldn't re-sync these subtitles. Its log says why.";
                    }

                    return null;
                }
                catch (HttpRequestException ex)
                {
                    LogSyncFailed(log, ex, path);
                    return ex.Message;
                }
                catch (TaskCanceledException) when (!stopping.IsCancellationRequested)
                {
                    return "Bazarr took too long.";
                }
            });
            return started
                ? Results.Accepted(value: new SubtitleSyncDto("running", track.Key))
                : Results.Problem("A re-sync of this video is already running.", statusCode: StatusCodes.Status409Conflict);
        });

        group.MapGet("/sync", async (Guid id, Guid? fileId, FlexarrDbContext db, SubtitleSyncs syncs, CancellationToken ct) =>
        {
            var files = await PlayableFilesAsync(db, id, ct);
            return Choose(files, fileId) is { } file && syncs.Get(file.Id) is { } state ? Results.Ok(state) : Results.Ok(new SubtitleSyncDto("none"));
        });

        return api;
    }

    /// <summary>How long a download may take Bazarr, and a re-sync (which listens to the whole film).</summary>
    internal static TimeSpan DownloadWait { get; set; } = TimeSpan.FromSeconds(90);

    internal static TimeSpan SyncWait { get; set; } = TimeSpan.FromMinutes(15);

    internal static TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits for work Bazarr queued (since 1.6 it answers at once): until <paramref name="done"/> finds what it made, or the job
    /// Bazarr started for it ends without that, or time runs out. Null unless it was made.
    /// </summary>
    /// <param name="before">The jobs Bazarr had before, so an older one with the same name isn't taken for this one.</param>
    private static async Task<T?> WaitForBazarrAsync<T>(BazarrClient bazarr, BazarrInstance instance, HashSet<int> before, Func<BazarrJob, bool> ours,
        Func<T?> done, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var until = DateTime.UtcNow + timeout;
        while (done() is null)
        {
            if (DateTime.UtcNow > until)
            {
                return null;
            }

            await Task.Delay(PollEvery, ct);
            var jobs = await JobsAsync(bazarr, instance, ct);
            if (jobs.Any(j => !before.Contains(j.Id) && ours(j) && j.Status is "failed" or "completed"))
            {
                // The file is written before the job ends: one last look.
                return done();
            }
        }

        return done();
    }

    private static async Task<HashSet<int>> JobIdsAsync(BazarrClient bazarr, BazarrInstance instance, CancellationToken ct) =>
        (await JobsAsync(bazarr, instance, ct)).Select(j => j.Id).ToHashSet();

    /// <summary>Bazarr's jobs, or none when it won't say: waiting then falls back to watching the file.</summary>
    private static async Task<IReadOnlyList<BazarrJob>> JobsAsync(BazarrClient bazarr, BazarrInstance instance, CancellationToken ct)
    {
        try
        {
            return await bazarr.JobsAsync(instance, ct);
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    /// <summary>The best of a search's results: the top one, preferring plain subtitles over hearing-impaired or forced ones.</summary>
    internal static BazarrSubtitleResult? Best(IReadOnlyList<BazarrSubtitleResult> results) =>
        results.FirstOrDefault(r => !r.HearingImpaired && !r.Forced) ?? results.FirstOrDefault(r => !r.Forced) ?? (results.Count > 0 ? results[0] : null);

    private sealed record Found(IResult? Problem, BazarrInstance? Bazarr = null, BazarrMedia? Media = null, MediaFile? File = null);

    /// <summary>The Bazarr film or episode for an item's file, given a languages profile if it had none (Bazarr won't search without one).</summary>
    private static async Task<Found> FindAsync(Guid id, Guid? fileId, IOptionsMonitor<FlexarrOptions> options, BazarrClient bazarr, FlexarrDbContext db, CancellationToken ct)
    {
        if (options.CurrentValue.Bazarr?.Instance is not { } instance)
        {
            return new Found(Results.Problem("Bazarr isn't set up. Add it in Settings › Connections.", statusCode: StatusCodes.Status404NotFound));
        }

        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return new Found(Results.NotFound());
        }

        if (item.Kind is not (MediaKind.Movie or MediaKind.Episode))
        {
            return new Found(Results.Problem("Subtitles can be found for films and episodes.", statusCode: StatusCodes.Status400BadRequest));
        }

        if (item.ArrId is not { } arrId)
        {
            return new Found(Results.Problem($"Bazarr finds subtitles for what {(item.Kind == MediaKind.Movie ? "Radarr" : "Sonarr")} manages, and this isn't one of them.",
                statusCode: StatusCodes.Status409Conflict));
        }

        if (Choose(await PlayableFilesAsync(db, id, ct), fileId) is not { } file)
        {
            return new Found(Results.Problem("This has no file to find subtitles for.", statusCode: StatusCodes.Status404NotFound));
        }

        try
        {
            var media = item.Kind == MediaKind.Movie ? await bazarr.MovieAsync(instance, arrId, ct) : await bazarr.EpisodeAsync(instance, arrId, ct);
            if (media is null)
            {
                return new Found(Results.Problem("Bazarr doesn't have this yet. It picks up new titles from Sonarr and Radarr every so often.",
                    statusCode: StatusCodes.Status409Conflict));
            }

            // Subtitles land beside Bazarr's copy, so they'd be no use to another one.
            if (!FileName(media.Path).Equals(Path.GetFileName(file.Path), StringComparison.OrdinalIgnoreCase))
            {
                return new Found(Results.Problem($"Bazarr finds subtitles for another copy of this ({FileName(media.Path)}). Play that one to get them.",
                    statusCode: StatusCodes.Status409Conflict));
            }

            if (media.ProfileId is null)
            {
                if (await bazarr.ProfilesAsync(instance, ct) is not [var profile, ..])
                {
                    return new Found(Results.Problem("Add a languages profile in Bazarr first: it searches only in the languages a profile lists.",
                        statusCode: StatusCodes.Status409Conflict));
                }

                await bazarr.SetProfileAsync(instance, media, profile, ct);
                media = media with { ProfileId = profile };
            }

            return new Found(null, instance, media, file);
        }
        catch (HttpRequestException ex)
        {
            return new Found(Results.Problem($"Bazarr can't be reached: {ex.Message}", statusCode: StatusCodes.Status502BadGateway));
        }
    }

    private static Task<List<MediaFile>> PlayableFilesAsync(FlexarrDbContext db, Guid id, CancellationToken ct) =>
        db.MediaFiles.AsNoTracking().Include(f => f.Streams).Where(f => f.MediaItemId == id && f.ProbeError == null).ToListAsync(ct);

    /// <summary>The requested copy, or the one playback would choose.</summary>
    private static MediaFile? Choose(List<MediaFile> files, Guid? fileId) =>
        files.FirstOrDefault(f => f.Id == fileId) ?? files.OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefault();

    /// <summary>The name of a file as Bazarr gives it, whichever way its slashes go.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static SubtitleResultDto ToDto(BazarrSubtitleResult r) => new(
        Encode(r),
        r.Provider,
        r.Language,
        Languages.Label(r.Language, r.HearingImpaired ? "SDH" : null, r.Forced),
        r.ReleaseInfo.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))?.Trim() ?? "",
        r.HearingImpaired,
        r.Forced,
        (int)Math.Round(r.Score),
        r.Matches.Contains("hash", StringComparer.OrdinalIgnoreCase),
        r.Uploader);

    // A result travels to the client and back as its own id, so the server keeps nothing between search and download.
    private static string Encode(BazarrSubtitleResult r) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Token(r.Provider, r.Subtitle, r.Language, r.HearingImpaired, r.Forced, r.OriginalFormat), Json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static BazarrSubtitleResult? Decode(string id)
    {
        try
        {
            var base64 = id.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
            return JsonSerializer.Deserialize<Token>(bytes, Json) is { Provider.Length: > 0, Subtitle.Length: > 0 } t
                ? new BazarrSubtitleResult(t.Provider, t.Subtitle, t.Language, t.Hi, t.Forced, t.OriginalFormat, 0, [], [], null)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Token(string Provider, string Subtitle, string Language, bool Hi, bool Forced, bool OriginalFormat);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bazarr was asked for subtitles for {Path}, but none appeared beside the file")]
    private static partial void LogNotSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bazarr was asked to re-sync {Path}, but the file didn't change")]
    private static partial void LogSyncUnchanged(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bazarr couldn't re-sync {Path}")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception, string path);

    private static async Task<bool> SafePingAsync(BazarrClient bazarr, BazarrInstance instance, CancellationToken ct)
    {
        try
        {
            return await bazarr.PingAsync(instance, ct);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

/// <summary>Re-syncs running in the background, one per video file, and how the last one ended.</summary>
public sealed class SubtitleSyncs(IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<Guid, SubtitleSyncDto> states = new();
    private readonly Lock gate = new();

    public SubtitleSyncDto? Get(Guid fileId) => states.GetValueOrDefault(fileId);

    /// <summary>Starts a re-sync unless one of the same file is running.</summary>
    /// <param name="sync">Returns an error to show, or null when it worked.</param>
    public bool TryStart(Guid fileId, string key, Func<CancellationToken, Task<string?>> sync)
    {
        lock (gate)
        {
            if (states.TryGetValue(fileId, out var current) && current.State == "running")
            {
                return false;
            }

            states[fileId] = new SubtitleSyncDto("running", key);
        }

        _ = Task.Run(async () =>
        {
            var error = await sync(lifetime.ApplicationStopping);
            states[fileId] = error is null ? new SubtitleSyncDto("done", key) : new SubtitleSyncDto("failed", key, error);
        });
        return true;
    }
}
