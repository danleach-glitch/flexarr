using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record LyricLineDto(double? Time, string Text);

/// <summary>A track's lyrics; synced ones have a time (seconds) on every line.</summary>
public sealed record LyricsDto(bool Synced, IReadOnlyList<LyricLineDto> Lines);

public static class LyricsEndpoints
{
    public static RouteGroupBuilder MapLyricsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/lyrics", async (Guid id, FlexarrDbContext db, LyricsCache lyrics, CancellationToken ct) =>
        {
            var file = await db.MediaFiles.AsNoTracking()
                .Where(f => f.MediaItemId == id && f.MediaItem!.Kind == MediaKind.Track && f.MissingSince == null)
                .OrderBy(f => f.Path)
                .FirstOrDefaultAsync(ct);
            if (file is null)
            {
                return Results.NotFound();
            }

            return await lyrics.GetAsync(file, ct) is { } found
                ? Results.Ok(new LyricsDto(found.Synced, found.Lines.Select(l => new LyricLineDto(l.Time, l.Text)).ToList()))
                : Results.NotFound();
        }).WithTags("Items");

        return api;
    }
}

/// <summary>
/// Finds a track's lyrics when they're first asked for: an .lrc sidecar, the file's own tags (which means running ffprobe
/// again, as the scanner doesn't keep them), or a .txt sidecar. The answer, including "none", is kept on disk under
/// cache/lyrics so the next request doesn't touch the track, until the track or a sidecar changes.
/// </summary>
public sealed partial class LyricsCache(IMediaProbe probe, IOptionsMonitor<FlexarrOptions> options, ILogger<LyricsCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Lyrics?> GetAsync(MediaFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var lrc = Path.ChangeExtension(file.Path, ".lrc");
        var txt = Path.ChangeExtension(file.Path, ".txt");
        var key = string.Join('|', Stamp(file.Path), Stamp(lrc), Stamp(txt));
        var cachePath = Path.Combine(options.CurrentValue.ResolvedCacheDirectory, "lyrics", file.Id.ToString("N") + ".json");

        if (ReadCached(cachePath) is { } cached && cached.Key == key)
        {
            return cached.Lyrics;
        }

        var lyrics = await ReadAsync(file.Path, ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new Entry(key, lyrics), Json), ct);
        }
        catch (IOException e)
        {
            LogCacheWriteFailed(logger, cachePath, e.Message);
        }

        return lyrics;
    }

    /// <summary>Everything the track has, in the order sources are preferred when none is synced; a synced one wins.</summary>
    private async Task<Lyrics?> ReadAsync(string trackPath, CancellationToken ct)
    {
        var found = new List<Lyrics>();
        if (LyricsParser.Sidecar(trackPath, ".lrc") is { } lrc && LyricsParser.Parse(await File.ReadAllTextAsync(lrc, ct)) is { } fromLrc)
        {
            if (fromLrc.Synced)
            {
                return fromLrc;
            }

            found.Add(fromLrc);
        }

        if (File.Exists(trackPath))
        {
            try
            {
                var probed = await probe.ProbeAsync(trackPath, ct);
                if (LyricsParser.FromTags(probed.Tags) is { } fromTags)
                {
                    found.Insert(0, fromTags); // Embedded lyrics travel with the file, so they're trusted first.
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A file ffprobe can't read has no lyrics to offer; the scanner reports the file itself.
                LogProbeFailed(logger, trackPath, e.Message);
            }
        }

        if (LyricsParser.Sidecar(trackPath, ".txt") is { } txt && LyricsParser.Parse(await File.ReadAllTextAsync(txt, ct)) is { } fromTxt)
        {
            found.Add(fromTxt);
        }

        return found.FirstOrDefault(l => l.Synced) ?? found.FirstOrDefault();
    }

    /// <summary>When a file last changed, or "-" when it isn't there: a sidecar appearing or going changes the key too.</summary>
    private static string Stamp(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    private Entry? ReadCached(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            LogCacheReadFailed(logger, path, e.Message);
            return null;
        }
    }

    private sealed record Entry(string Key, Lyrics? Lyrics);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't probe {Path} for lyrics: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't write the lyrics cache {Path}: {Reason}")]
    private static partial void LogCacheWriteFailed(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring the unreadable lyrics cache {Path}: {Reason}")]
    private static partial void LogCacheReadFailed(ILogger logger, string path, string reason);
}
